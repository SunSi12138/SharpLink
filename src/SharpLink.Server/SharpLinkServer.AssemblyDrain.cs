using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
        Assembly assembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfRegistrationCallbackReentry();
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        while (true)
        {
            SharpLinkDynamicModule module;
            ServerServiceSnapshot.Lease snapshot;
            lock (_registryGate)
            {
                if (_unregisterOperations.TryGetValue(assembly, out var existing))
                    return WaitForUnregisterAsync(existing, cancellationToken);
                if (!_dynamicModules.TryGetValue(assembly, out module!))
                    return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
                snapshot = _serviceModuleRegistry.CaptureServices();
            }
            using (snapshot)
            using (var storage = _runtimeContext.CreateMetadataStorage())
            {
                ServerServiceSnapshot? old = null;
                ServerServiceSnapshot? next = null;
                ServerDetachedServices? detached = null;
                var transferred = false;
                TaskCompletionSource<SharpLinkAssemblyUnregisterResult>? completion = null;
                try
                {
                    var source = snapshot.Services;
                    var count = source.Values.Count(service => ReferenceEquals(service.Module, module));
                    var detachedCharge = storage.ReserveCollection(count);
                    var retiringBindings = source.Values.Where(service => ReferenceEquals(service.Module, module)).ToArray();
                    detached = new ServerDetachedServices(retiringBindings, storage, detachedCharge);
                    using var mutableCharge = storage.ReserveCollection(source.Count - count);
                    var nextMap = source.Where(pair => !ReferenceEquals(pair.Value.Module, module))
                        .ToDictionary(static pair => pair.Key, static pair => pair.Value);
                    var snapshotCharge = storage.ReserveCollection(nextMap.Count);
                    next = new ServerServiceSnapshot(nextMap.ToFrozenDictionary(), storage, snapshotCharge);
                    nextMap.Clear();
                    lock (_registryGate)
                    {
                        if (_unregisterOperations.TryGetValue(assembly, out var existing))
                            return WaitForUnregisterAsync(existing, cancellationToken);
                        if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module))
                            return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
                        if (!_serviceModuleRegistry.IsCurrentServices(snapshot)) continue;
                        EnsureNoDynamicDependants(module);
                        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (_detachedModuleServices.TryGetValue(module, out _))
                        {
                            _unregisterOperations.Add(assembly, completion.Task);
                        }
                        else
                        {
                            var generation = NextBindingPublicationGeneration();
                            _unregisterOperations.Add(assembly, completion.Task);
                            try { _detachedModuleServices.Add(module, detached); }
                            catch { _unregisterOperations.Remove(assembly); throw; }
                            old = _serviceModuleRegistry.PublishServices(next);
                            transferred = true;
                            module.TryBeginDraining();
                            RetireBindingGroup(retiringBindings);
                            _registryGeneration = generation;
                        }
                        module.TryBeginDraining();
                    }
                }
                finally
                {
                    old?.Dispose(); // Cold quota release is outside the registry gate.
                    if (!transferred) { next?.Dispose(); detached?.Dispose(); }
                }
                ScheduleBindingInvalidations();
                ScheduleContractManifestPublish();
                _ = CompleteUnregisterOperationAsync(assembly, module, gracefulTimeout, completion!);
                TrackFrameworkTask(completion!.Task, "DynamicAssemblyUnregister", TaskObservationMode.ExternallyObserved);
                return WaitForUnregisterAsync(completion.Task, cancellationToken);
            }
        }
    }

    private static ValueTask<SharpLinkAssemblyUnregisterResult> WaitForUnregisterAsync(
        Task<SharpLinkAssemblyUnregisterResult> operation,
        CancellationToken cancellationToken)
        => new SharpLinkRetirementHandle<SharpLinkAssemblyUnregisterResult>(operation)
            .WaitAsync(cancellationToken);

    private async Task<SharpLinkAssemblyUnregisterResult> UnregisterCoreAsync(
        Assembly assembly,
        SharpLinkDynamicModule module,
        TimeSpan gracefulTimeout)
    {
        module.TryBeginDraining();
        var drainTask = module.WaitForDrainAsync();
        if (!drainTask.IsCompleted)
        {
            if (!await SharpLinkDynamicModule.WaitForDrainAsync(
                    drainTask,
                    gracefulTimeout,
                    _runtimeContext.TimeProvider).ConfigureAwait(false))
            {
                module.CancelRemainingCalls();
                await Task.Yield();
                if (!drainTask.IsCompleted)
                {
                    module.MarkDrainTimedOut();
                    TrackFrameworkTask(
                        CompleteTimedOutUnregisterAsync(assembly, module, drainTask),
                        "DynamicAssemblyTimedOutUnregisterCleanup");
                    return new SharpLinkAssemblyUnregisterResult
                    {
                        ReferencesReleased = false,
                        RemainingCalls = module.RemainingCalls,
                        RemainingStreams = module.RemainingStreams
                    };
                }
            }
        }

        await ReleaseModuleAsync(assembly, module).ConfigureAwait(false);
        return new SharpLinkAssemblyUnregisterResult { ReferencesReleased = true };
    }

    private async Task CompleteUnregisterOperationAsync(
        Assembly assembly,
        SharpLinkDynamicModule module,
        TimeSpan gracefulTimeout,
        TaskCompletionSource<SharpLinkAssemblyUnregisterResult> completion)
    {
        try
        {
            completion.TrySetResult(await UnregisterCoreAsync(
                assembly, module, gracefulTimeout).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_registryGate)
                _unregisterOperations.Remove(assembly);
        }
    }

    private async Task CompleteTimedOutUnregisterAsync(
        Assembly assembly,
        SharpLinkDynamicModule module,
        Task drainTask)
    {
        await drainTask.ConfigureAwait(false);
        await ReleaseModuleAsync(assembly, module).ConfigureAwait(false);
    }

    private async Task ReleaseModuleAsync(Assembly assembly, SharpLinkDynamicModule module)
    {
        // This exact existing module fence now includes cold preparations and the single
        // post-permit physical invocation lease; there is no binding-group call counter.
        await module.WaitForDrainAsync().ConfigureAwait(false);
        ServiceRegistration[] removedServices;
        ServerDetachedServices? detachedOwner = null;
        RpcGeneratedManifestRegistration codecRegistration;
        while (true)
        {
            ServerServiceSnapshot.Lease snapshot;
            lock (_registryGate)
            {
                if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module)) return;
                snapshot = _serviceModuleRegistry.CaptureServices();
                codecRegistration = module.CodecRegistration;
            }
            using (snapshot)
            using (var storage = _runtimeContext.CreateMetadataStorage())
            {
                using var workspace = new ServerMetadataWorkspace(storage);
                var services = snapshot.Services;
                workspace.Reserve(services.Count);
                var nextMap = services.Where(pair => !ReferenceEquals(pair.Value.Module, module))
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value);
                workspace.ClearBeforeRelease(nextMap.Clear);
                var mapCharge = storage.ReserveCollection(nextMap.Count);
                ServerServiceSnapshot? next = null;
                ServerServiceSnapshot? old = null;
                ServerDetachedServices? fallback = null;
                var published = false;
                try
                {
                    next = new ServerServiceSnapshot(nextMap.ToFrozenDictionary(), storage, mapCharge);
                    var factories = _runtimeContext.CreateGeneratedCodecSnapshot();
                    workspace.Reserve(factories.Count);
                    var nextFactories = factories.ToDictionary(static pair => pair.Key, static pair => pair.Value);
                    workspace.ClearBeforeRelease(nextFactories.Clear);
                    var count = services.Values.Count(service => ReferenceEquals(service.Module, module));
                    var fallbackCharge = storage.ReserveCollection(count);
                    fallback = new ServerDetachedServices(services.Values
                        .Where(service => ReferenceEquals(service.Module, module)).ToArray(), storage, fallbackCharge);
                    lock (_registryGate)
                    {
                        if (!_serviceModuleRegistry.IsCurrentServices(snapshot)) continue;
                        foreach (var codecType in codecRegistration.Codecs.Keys)
                        {
                            var replacement = FindReplacementCodec(codecType, module);
                            if (replacement is null) nextFactories.Remove(codecType);
                            else nextFactories[codecType] = replacement;
                        }
                    }
                    // Fallible wrapper construction/validation is outside the registry gate.
                    using var publication = _runtimeContext.PrepareGeneratedCodecPublication(nextFactories);
                    lock (_registryGate)
                    {
                        if (!_serviceModuleRegistry.IsCurrentServices(snapshot) ||
                            !_runtimeContext.IsCurrentGeneratedCodecPublication(publication)) continue;
                        if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module)) return;
                        _runtimeContext.CommitGeneratedCodecPublication(publication);
                        old = _serviceModuleRegistry.PublishServices(next);
                        _dynamicModules.Remove(assembly);
                        if (!_detachedModuleServices.Remove(module, out detachedOwner!))
                        { detachedOwner = fallback; fallback = null; }
                        removedServices = detachedOwner.Services;
                        published = true;
                    }
                }
                finally
                {
                    old?.Dispose();
                    if (!published) next?.Dispose();
                    fallback?.Dispose();
                }
                break;
            }
        }

        List<Exception>? failures = null;
        var connections = _connectionRegistry.SnapshotOwned();
        foreach (var connection in connections)
        {
            for (var index = 0; index < removedServices.Length; index++)
            {
                try
                {
                    await connection.DisposeServiceAsync(removedServices[index]).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }
        }
        for (var index = 0; index < removedServices.Length; index++)
        {
            try
            {
                await removedServices[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        try
        {
            _runtimeContext.ReleaseGeneratedManifest(codecRegistration);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        finally
        {
            module.MarkReleased();
            detachedOwner?.Dispose();
        }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
    }

    private RpcGeneratedCodecRegistration? FindReplacementCodec(
        Type targetType,
        SharpLinkDynamicModule removedModule)
    {
        for (var index = 0; index < _staticManifests.Count; index++)
        {
            var replacement = _runtimeContext.FindGeneratedCodec(_staticManifests[index], targetType);
            if (replacement is not null)
                return replacement;
        }
        foreach (var candidate in _dynamicModules.Values)
        {
            if (ReferenceEquals(candidate, removedModule))
                continue;
            if (candidate.CodecRegistration.Codecs.TryGetValue(targetType, out var replacement))
                return replacement;
        }
        return null;
    }

    private void BeginDrainDynamicModules()
    {
        SharpLinkDynamicModule[] modules;
        lock (_registryGate)
        {
            modules = [.. _dynamicModules.Values];
            var currentServices = _services.Values.ToArray();
            var detachedServices = _detachedModuleServices.Values.ToArray();
            foreach (var module in modules)
                module.TryBeginDraining();
            RetireBindingGroup(currentServices);
            foreach (var registrations in detachedServices)
                RetireBindingGroup(registrations);
        }
        ScheduleBindingInvalidations();
        for (var index = 0; index < modules.Length; index++)
            modules[index].TryBeginDraining();
    }

    private async Task ReleaseDrainedDynamicModulesAsync()
    {
        KeyValuePair<Assembly, SharpLinkDynamicModule>[] modules;
        lock (_registryGate)
            modules = [.. _dynamicModules];

        var manifests = modules.Select(static pair => pair.Value.Manifest).ToArray();
        var order = SharpLinkGeneratedDependencyBinding.GetDependantsFirstOrder(manifests);
        List<Exception>? failures = null;
        for (var index = 0; index < order.Length; index++)
        {
            var pair = modules[order[index]];
            try
            {
                pair.Value.TryBeginDraining();
                await pair.Value.WaitForDrainAsync().ConfigureAwait(false);
                await ReleaseModuleAsync(pair.Key, pair.Value).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
    }

    private void EnsureNoDynamicDependants(SharpLinkDynamicModule module)
    {
        var ownerAssembly = module.Manifest.OwnerAssembly;
        var identity = ownerAssembly.FullName;
        foreach (var candidate in _dynamicModules.Values)
        {
            if (ReferenceEquals(candidate, module))
                continue;
            if (ManifestDependsOn(candidate.Manifest, ownerAssembly))
            {
                throw new InvalidOperationException(
                    $"Assembly '{identity}' cannot be unregistered while '{candidate.Manifest.OwnerAssembly.FullName}' depends on it.");
            }
        }
    }


}
