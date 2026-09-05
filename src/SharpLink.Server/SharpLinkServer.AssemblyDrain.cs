using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
        Assembly assembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        Task<SharpLinkAssemblyUnregisterResult> operation;
        lock (_registryGate)
        {
            if (_unregisterOperations.TryGetValue(assembly, out operation!))
                return WaitForUnregisterAsync(operation, cancellationToken);
            if (!_dynamicModules.TryGetValue(assembly, out var module))
            {
                return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult
                {
                    ReferencesReleased = false
                });
            }
            EnsureNoDynamicDependants(module);
            var completion = new TaskCompletionSource<SharpLinkAssemblyUnregisterResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            operation = completion.Task;
            _unregisterOperations.Add(assembly, operation);
            _ = CompleteUnregisterOperationAsync(assembly, module, gracefulTimeout, completion);
            TrackFrameworkTask(
                operation,
                "DynamicAssemblyUnregister",
                TaskObservationMode.ExternallyObserved);
        }
        return WaitForUnregisterAsync(operation, cancellationToken);
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
        ServiceRegistration[] removedServices;
        RpcGeneratedManifestRegistration codecRegistration;
        lock (_registryGate)
        {
            if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module))
                return;
            var services = Volatile.Read(ref _services);
            if (!_detachedModuleServices.Remove(module, out removedServices!))
            {
                removedServices = services.Values
                    .Where(service => ReferenceEquals(service.Module, module))
                    .ToArray();
            }
            var nextServices = services
                .Where(pair => !ReferenceEquals(pair.Value.Module, module))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                .ToFrozenDictionary();
            var factories = _runtimeContext.CreateGeneratedCodecSnapshot();
            codecRegistration = module.CodecRegistration;
            var removedCodecTypes = codecRegistration.Codecs.Keys.ToArray();
            var nextFactories = factories.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            for (var index = 0; index < removedCodecTypes.Length; index++)
            {
                var codecType = removedCodecTypes[index];
                var replacement = FindReplacementCodec(codecType, module);
                if (replacement is null)
                    nextFactories.Remove(codecType);
                else
                    nextFactories[codecType] = replacement;
            }
            _runtimeContext.PublishGeneratedCodecs(nextFactories);
            Volatile.Write(ref _services, nextServices);
            _dynamicModules.Remove(assembly);
            _registryGeneration++;
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
            modules = [.. _dynamicModules.Values];
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
