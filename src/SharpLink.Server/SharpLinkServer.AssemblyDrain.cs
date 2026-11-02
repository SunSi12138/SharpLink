using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
        Assembly assembly, TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        ThrowIfRegistrationCallbackReentry();
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        RetirementGraceBudget budget;
        using (RetirementCallbackScope.Enter(_runtimeContext.Domain))
            budget = RetirementGraceBudget.Capture(gracefulTimeout, _runtimeContext.TimeProvider);
        return UnregisterAssemblyCore(assembly, budget, cancellationToken);
    }

    private ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyCore(
        Assembly assembly, RetirementGraceBudget budget, CancellationToken cancellationToken)
    {
        while (true)
        {
            SharpLinkDynamicModule module;
            ServerServiceSnapshot.Lease? snapshot = null;
            Task<SharpLinkAssemblyUnregisterResult>? operation = null;
            Task<(Exception? Failure, bool SupervisorOwned)>? observation = null;
            RetirementControl? control = null;
            lock (_registryGate)
            {
                if (_unregisterOperations.TryGetValue(assembly, out var existing))
                {
                    operation = existing.Task; module = existing.Module;
                    observation = module.RetirementObservation; control = module.RetirementControl;
                }
                else
                {
                    if (!_dynamicModules.TryGetValue(assembly, out module!))
                        return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
                    snapshot = _serviceModuleRegistry.CaptureServices();
                }
            }
            if (operation is not null)
                return control is { IsCommitted: false }
                    ? RetryUnregisterAfterStageAsync(assembly, budget, operation, observation!, cancellationToken)
                    : WaitForControlledRetirementAsync(operation, control!, observation!, cancellationToken);

            ColdMetadataPreparation? storage = null;
            ColdMetadataReservation? mutableCharge = null;
            ServerServiceSnapshot? old = null;
            ServerServiceSnapshot? next = null;
            ServerDetachedServices? detached = null;
            RetirementControl? selected = null;
            var transferred = false;
            var retry = false;
            Exception? failure = null;
            Exception? CompleteActions(bool committed)
            {
                Exception? outcome = failure;
                AttemptRetirementCleanup(old, ref outcome);
                if (!transferred)
                {
                    AttemptRetirementCleanup(next, ref outcome);
                    AttemptRetirementCleanup(detached, ref outcome);
                }
                AttemptRetirementCleanup(mutableCharge, ref outcome);
                AttemptRetirementCleanup(snapshot, ref outcome);
                AttemptRetirementCleanup(storage, ref outcome);
                if (committed)
                {
                    try { ScheduleBindingInvalidations(); }
                    catch (Exception exception) { AddRetirementFailure(ref outcome, exception); }
                    try { ScheduleContractManifestPublish(); }
                    catch (Exception exception) { AddRetirementFailure(ref outcome, exception); }
                }
                return outcome;
            }
            try
            {
                storage = _runtimeContext.CreateMetadataStorage();
                var source = snapshot!.Services;
                var retiringBindings = source.Values.Where(service => ReferenceEquals(service.Module, module)).ToArray();
                var detachedCharge = storage.ReserveCollection(retiringBindings.Length);
                detached = new ServerDetachedServices(retiringBindings, storage, detachedCharge);
                mutableCharge = storage.ReserveCollection(source.Count - retiringBindings.Length);
                var nextMap = source.Where(pair => !ReferenceEquals(pair.Value.Module, module))
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value);
                var snapshotCharge = storage.ReserveCollection(nextMap.Count);
                next = new ServerServiceSnapshot(nextMap.ToFrozenDictionary(), storage, snapshotCharge);
                nextMap.Clear();
                lock (_registryGate)
                {
                    if (_unregisterOperations.TryGetValue(assembly, out var existing))
                    {
                        operation = existing.Task; module = existing.Module;
                        observation = module.RetirementObservation; control = module.RetirementControl;
                    }
                    else if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module) ||
                        !_serviceModuleRegistry.IsCurrentServices(snapshot!)) retry = true;
                    else
                    {
                        EnsureNoDynamicDependants(module);
                        var generation = NextBindingPublicationGeneration();
                        _detachedModuleServices.EnsureCapacity(checked(_detachedModuleServices.Count + 1));
                        selected = CreateRetirementLocked(assembly, module, RetirementOrigin.PublicUnregister, budget, CompleteActions);
                        control = selected;
                        operation = module.RetirementOperation;
                        observation = module.RetirementObservation;
                        // Rooted task precedes the first destructive admission cut.
                        // The remainder is pre-sized dictionary/scalar publication.
                        module.TryBeginDraining();
                        selected.MarkCommitted();
                        if (!_detachedModuleServices.TryGetValue(module, out _))
                        {
                            _detachedModuleServices.Add(module, detached);
                            old = _serviceModuleRegistry.PublishServices(next);
                            transferred = true;
                            RetireBindingGroup(retiringBindings);
                            _registryGeneration = generation;
                        }
                    }
                }
            }
            catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
            finally
            {
                if (selected is not null) selected.Activate();
                else failure = CompleteActions(committed: false);
            }
            if (selected is null) ThrowRetirementFailure(failure);
            if (retry) continue;
            return control is { IsCommitted: false }
                ? RetryUnregisterAfterStageAsync(assembly, budget, operation!, observation!, cancellationToken)
                : WaitForControlledRetirementAsync(operation!, control!, observation!, cancellationToken);
        }
    }

    private async Task ReleaseModuleAsync(Assembly assembly, SharpLinkDynamicModule module)
    {
        Task<Exception?>? existing;
        RpcGeneratedManifestRegistration? codecRegistration = null;
        IDisposable? metadata = null;
        lock (_registryGate)
        {
            existing = module.CleanupCompletion?.Task;
            if (existing is null)
            {
                if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module))
                    throw new InvalidOperationException("An unclaimed retirement module is no longer registered.");
                codecRegistration = module.CodecRegistration;
                metadata = codecRegistration.MetadataLifetime.Retain();
            }
        }
        if (existing is not null)
        { ThrowRetirementFailure(await existing.ConfigureAwait(false)); return; }

        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var claimed = false;
        ServerDetachedServices? detachedOwner = null;
        try
        {
            while (!claimed && existing is null)
            {
                ServerServiceSnapshot.Lease? snapshot = null;
                ColdMetadataPreparation? storage = null;
                SharpLinkRuntimeContext.CapturedGeneratedCodecs? codecSource = null;
                SharpLinkRuntimeContext.PreparedGeneratedCodecPublication? publication = null;
                ServerMetadataWorkspace? workspace = null;
                ServerServiceSnapshot? next = null;
                ServerServiceSnapshot? old = null;
                ServerDetachedServices? fallback = null;
                var published = false;
                var retry = false;
                try
                {
                    lock (_registryGate)
                    {
                        existing = module.CleanupCompletion?.Task;
                        if (existing is not null) continue;
                        if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module))
                            throw new InvalidOperationException("The exact cleanup module disappeared before claim.");
                        snapshot = _serviceModuleRegistry.CaptureServices();
                    }
                    storage = _runtimeContext.CreateMetadataStorage();
                    codecSource = _runtimeContext.CaptureGeneratedCodecSnapshot();
                    workspace = new ServerMetadataWorkspace(storage);
                    var services = snapshot.Services;
                    workspace.Reserve(services.Count);
                    var nextMap = services.Where(pair => !ReferenceEquals(pair.Value.Module, module))
                        .ToDictionary(static pair => pair.Key, static pair => pair.Value);
                    workspace.ClearBeforeRelease(nextMap.Clear);
                    var mapCharge = storage.ReserveCollection(nextMap.Count);
                    next = new ServerServiceSnapshot(nextMap.ToFrozenDictionary(), storage, mapCharge);
                    var factories = codecSource.Registrations;
                    workspace.Reserve(factories.Count);
                    var nextFactories = factories.ToDictionary(static pair => pair.Key, static pair => pair.Value);
                    workspace.ClearBeforeRelease(nextFactories.Clear);
                    var count = services.Values.Count(service => ReferenceEquals(service.Module, module));
                    var fallbackCharge = storage.ReserveCollection(count);
                    fallback = new ServerDetachedServices(services.Values
                        .Where(service => ReferenceEquals(service.Module, module)).ToArray(), storage, fallbackCharge);
                    lock (_registryGate)
                    {
                        foreach (var codecType in codecRegistration!.Codecs.Keys)
                        {
                            var replacement = FindReplacementCodec(codecType, module, codecSource);
                            if (replacement is null) nextFactories.Remove(codecType);
                            else nextFactories[codecType] = replacement;
                        }
                    }
                    publication = _runtimeContext.PrepareGeneratedCodecPublication(nextFactories, sourceSnapshot: codecSource);
                    lock (_registryGate)
                    {
                        existing = module.CleanupCompletion?.Task;
                        if (existing is not null) continue;
                        if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module))
                            throw new InvalidOperationException("The exact cleanup module disappeared before publication.");
                        if (!_serviceModuleRegistry.IsCurrentServices(snapshot!) ||
                            !_runtimeContext.IsCurrentGeneratedCodecPublication(publication) ||
                            !ReplacementCodecsRemainCurrent(module, nextFactories, codecSource)) retry = true;
                        else
                        {
                            _runtimeContext.CommitGeneratedCodecPublication(publication);
                            // Every subsequent failure belongs to this winner, including host refund.
                            module.CleanupCompletion = completion;
                            claimed = true;
                            if (!_detachedModuleServices.Remove(module, out detachedOwner!))
                            { detachedOwner = fallback; fallback = null; }
                            old = _serviceModuleRegistry.PublishServices(next);
                            published = true;
                            _registryGeneration++;
                        }
                    }
                }
                catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
                finally
                {
                    AttemptRetirementCleanup(publication, ref failure);
                    AttemptRetirementCleanup(old, ref failure);
                    if (!published) AttemptRetirementCleanup(next, ref failure);
                    AttemptRetirementCleanup(fallback, ref failure);
                    AttemptRetirementCleanup(workspace, ref failure);
                    AttemptRetirementCleanup(codecSource, ref failure);
                    AttemptRetirementCleanup(storage, ref failure);
                    AttemptRetirementCleanup(snapshot, ref failure);
                }
                if (failure is not null || (!claimed && existing is null && !retry)) break;
            }
            if (claimed)
            {
                var services = detachedOwner!.Services;
                // Already-owned storage: no teardown quota admission or broad own-codec use.
                // This frame ends BEFORE the module's own codec retirement begins.
                using (codecRegistration!.Storage.EnterCallback())
                {
                    var connections = _connectionRegistry.SnapshotOwned();
                    foreach (var connection in connections)
                        foreach (var service in services)
                        {
                            try { await connection.DisposeServiceAsync(service).ConfigureAwait(false); }
                            catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
                        }
                    foreach (var service in services)
                    {
                        try { await service.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
                    }
                }
                try { _runtimeContext.ReleaseGeneratedManifest(codecRegistration); }
                catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
                lock (_registryGate)
                {
                    if (_dynamicModules.TryGetValue(assembly, out var current) && ReferenceEquals(current, module))
                    { _dynamicModules.Remove(assembly); _registryGeneration++; }
                    module.MarkReleased();
                }
            }
        }
        catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
        finally
        {
            if (claimed && module.State != SharpLinkDynamicModuleState.Released && detachedOwner is not null)
            {
                // An infrastructure failure before the callback sequence is not permission to
                // drop unvisited services. Retain the existing exact owner on the visible module.
                try
                {
                    lock (_registryGate) _detachedModuleServices.Add(module, detachedOwner);
                    detachedOwner = null;
                }
                catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
            }
            AttemptRetirementCleanup(detachedOwner, ref failure);
            AttemptRetirementCleanup(metadata, ref failure);
            if (claimed) completion.TrySetResult(failure);
        }
        if (existing is not null)
        {
            var original = await existing.ConfigureAwait(false);
            if (original is not null) AddRetirementFailure(ref failure, original);
        }
        ThrowRetirementFailure(failure);
    }

    private bool ReplacementCodecsRemainCurrent(SharpLinkDynamicModule removedModule,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> expected,
        SharpLinkRuntimeContext.CapturedGeneratedCodecs codecSource)
    {
        foreach (var type in removedModule.CodecRegistration.Codecs.Keys)
        {
            expected.TryGetValue(type, out var selected);
            if (!ReferenceEquals(selected, FindReplacementCodec(type, removedModule, codecSource))) return false;
        }
        return true;
    }

    private static void AddRetirementFailure(ref Exception? failure, Exception exception)
        => failure = failure is null ? exception : new AggregateException(failure, exception);

    private static void AttemptRetirementCleanup(IDisposable? resource, ref Exception? failure)
    {
        try { resource?.Dispose(); }
        catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
    }

    private static void ThrowRetirementFailure(Exception? failure)
    {
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private RpcGeneratedCodecRegistration? FindReplacementCodec(
        Type targetType,
        SharpLinkDynamicModule removedModule,
        SharpLinkRuntimeContext.CapturedGeneratedCodecs codecSource)
    {
        for (var index = 0; index < _staticManifests.Count; index++)
        {
            var replacement = codecSource.FindGeneratedCodec(_staticManifests[index], targetType);
            if (replacement is not null)
                return replacement;
        }
        foreach (var candidate in _dynamicModules.Values)
        {
            if (ReferenceEquals(candidate, removedModule) || candidate.State != SharpLinkDynamicModuleState.Running)
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
        if (modules.Length != 0) _lifecycle.MarkStrictPhysicalRegistration();
        ScheduleBindingInvalidations();
        for (var index = 0; index < modules.Length; index++)
            modules[index].TryBeginDraining();
    }

    private async Task<Exception?> ReleaseDrainedDynamicModulesAsync()
    {
        KeyValuePair<Assembly, SharpLinkDynamicModule>[] modules = [];
        KeyValuePair<Assembly, (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module)>[] terminal = [];
        ISharpLinkGeneratedAssemblyManifest[]? manifests = null;
        IDisposable?[]? metadata = null;
        int[] order = [];
        Exception? failure = null;
        try
        {
            lock (_registryGate)
            {
                modules = [.. _dynamicModules];
                manifests = new ISharpLinkGeneratedAssemblyManifest[modules.Length];
                metadata = new IDisposable?[modules.Length];
                for (var index = 0; index < modules.Length; index++)
                {
                    var owner = modules[index].Value.CodecRegistration;
                    metadata[index] = owner.MetadataLifetime.Retain();
                    manifests[index] = owner.Manifest;
                }
                terminal = _unregisterOperations.Entries.Where(pair =>
                    !_dynamicModules.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, pair.Value.Module)).ToArray();
            }
            // Resolution can invoke a custom AssemblyLoadContext: never run it under the host gate.
            using (_runtimeContext.EnterOwnedMetadataCallback())
                order = SharpLinkGeneratedDependencyBinding.GetDependantsFirstOrder(manifests);
        }
        catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
        finally
        {
            if (manifests is not null) Array.Clear(manifests);
            if (metadata is not null)
            {
                foreach (var lease in metadata) AttemptRetirementCleanup(lease, ref failure);
                Array.Clear(metadata);
            }
        }
        if (failure is not null) throw PhysicalTaskFailureException.From(failure);
        foreach (var pair in terminal)
        {
            var observed = await JoinRetirementForStopAsync(pair.Key, pair.Value.Module, pair.Value.Task, pair.Value.Module.RetirementObservation!).ConfigureAwait(false);
            if (observed.Failure is not null) AddRetirementFailure(ref failure, observed.Failure);
            if (!observed.Released)
                throw PhysicalTaskFailureException.From(failure ?? new InvalidOperationException("Unreleased terminal module retains its dependencies."));
        }
        foreach (var index in order)
        {
            var pair = modules[index];
            var retirement = await GetShutdownRetirementAsync(pair.Key, pair.Value).ConfigureAwait(false);
            var operation = retirement.Operation;
            var observation = retirement.Observation;
            var observed = await JoinRetirementForStopAsync(pair.Key, pair.Value, operation, observation).ConfigureAwait(false);
            if (observed.Failure is not null) AddRetirementFailure(ref failure, observed.Failure);
            // Capture release BEFORE dropping the operation: a concurrent retry is not this outcome.
            if (!observed.Released)
                throw PhysicalTaskFailureException.From(failure ?? new InvalidOperationException(
                    "Module retirement ended before release; dependent ownership remains retained."));
        }
        return failure;
    }

    private async Task<(Exception? Failure, bool Released)> JoinRetirementForStopAsync(Assembly assembly, SharpLinkDynamicModule module,
        Task<SharpLinkAssemblyUnregisterResult> operation,
        Task<(Exception? Failure, bool SupervisorOwned)> observation)
    {
        Exception? failure = null;
        try { await operation.ConfigureAwait(false); }
        catch (Exception exception) { failure = PhysicalTaskFailureException.GetRaw(exception); }
        var observed = await observation.ConfigureAwait(false);
        if (observed.SupervisorOwned) failure = null; // Its strict registration owns that raw task outcome.
        if (observed.Failure is not null) AddRetirementFailure(ref failure, observed.Failure);
        bool released;
        lock (_registryGate)
        {
            released = module.State == SharpLinkDynamicModuleState.Released;
            // Only this actual join adopts a receipt-null outcome. Shutdown intent alone does not.
            if (released && _unregisterOperations.TryGetValue(assembly, out var entry) &&
                ReferenceEquals(entry.Task, operation) && ReferenceEquals(entry.Module, module))
                _unregisterOperations.Remove(assembly);
        }
        return (failure, released);
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
