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
        while (true)
        {
            SharpLinkDynamicModule module;
            ServerServiceSnapshot.Lease? snapshot = null;
            Task<SharpLinkAssemblyUnregisterResult>? operation = null;
            Task<(Exception? Failure, bool SupervisorOwned)>? observation = null;
            Task<Exception?>? tracking = null;
            lock (_registryGate)
            {
                if (_unregisterOperations.TryGetValue(assembly, out var existing))
                { operation = existing.Task; module = existing.Module;
                    observation = module.RetirementObservation; tracking = module.RetirementTracking; }
                else
                {
                    if (!_dynamicModules.TryGetValue(assembly, out module!))
                        return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
                    snapshot = _serviceModuleRegistry.CaptureServices();
                }
            }
            if (operation is not null)
                return WaitForRetirementAsync(operation, module, observation!, tracking!, gracefulTimeout, cancellationToken);

            ColdMetadataPreparation? storage = null;
            ServerServiceSnapshot? old = null;
            ServerServiceSnapshot? next = null;
            ServerDetachedServices? detached = null;
            TaskCompletionSource<Exception?>? ready = null;
            var transferred = false;
            var retry = false;
            Exception? failure = null;
            try
            {
                storage = _runtimeContext.CreateMetadataStorage();
                var source = snapshot!.Services;
                var retiringBindings = source.Values.Where(service => ReferenceEquals(service.Module, module)).ToArray();
                var detachedCharge = storage.ReserveCollection(retiringBindings.Length);
                detached = new ServerDetachedServices(retiringBindings, storage, detachedCharge);
                using var mutableCharge = storage.ReserveCollection(source.Count - retiringBindings.Length);
                var nextMap = source.Where(pair => !ReferenceEquals(pair.Value.Module, module))
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value);
                var snapshotCharge = storage.ReserveCollection(nextMap.Count);
                next = new ServerServiceSnapshot(nextMap.ToFrozenDictionary(), storage, snapshotCharge);
                nextMap.Clear();
                lock (_registryGate)
                {
                    if (_unregisterOperations.TryGetValue(assembly, out var existing))
                    { operation = existing.Task; module = existing.Module;
                    observation = module.RetirementObservation; tracking = module.RetirementTracking; }
                    else if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module) ||
                        !_serviceModuleRegistry.IsCurrentServices(snapshot!)) retry = true;
                    else
                    {
                        EnsureNoDynamicDependants(module);
                        var generation = NextBindingPublicationGeneration();
                        operation = StartRetirementLocked(assembly, module, stopOwned: false, out ready);
                        observation = module.RetirementObservation; tracking = module.RetirementTracking;
                        if (!_detachedModuleServices.TryGetValue(module, out _))
                        {
                            _detachedModuleServices.Add(module, detached);
                            old = _serviceModuleRegistry.PublishServices(next);
                            transferred = true;
                            RetireBindingGroup(retiringBindings);
                            _registryGeneration = generation;
                        }
                        module.TryBeginDraining();
                    }
                }
            }
            catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
            finally
            {
                AttemptRetirementCleanup(old, ref failure);
                if (!transferred)
                { AttemptRetirementCleanup(next, ref failure); AttemptRetirementCleanup(detached, ref failure); }
                AttemptRetirementCleanup(snapshot, ref failure);
                AttemptRetirementCleanup(storage, ref failure);
                if (ready is not null)
                {
                    try { ScheduleBindingInvalidations(); }
                    catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
                    try { ScheduleContractManifestPublish(); }
                    catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
                    ready.TrySetResult(failure);
                }
            }
            if (ready is null) ThrowRetirementFailure(failure);
            if (retry) continue;
            return WaitForRetirementAsync(operation!, module, observation!, tracking!, gracefulTimeout, cancellationToken);
        }
    }

    // Existing registry gate elects and roots the ACTUAL suspended worker before it can start.
    private Task<SharpLinkAssemblyUnregisterResult> StartRetirementLocked(
        Assembly assembly, SharpLinkDynamicModule module, bool stopOwned,
        out TaskCompletionSource<Exception?> ready)
    {
        ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracking = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        module.RetirementRegistration = null;
        module.RetirementTracking = tracking.Task;
        var operation = RetireModulePhysicalAsync(assembly, module, stopOwned, ready.Task, tracking);
        module.RetirementOperation = operation;
        _unregisterOperations.Add(assembly, (operation, module));
        module.RetirementObservation = ObserveRetirementExitAsync(assembly, module, operation);
        return operation;
    }

    private async Task<SharpLinkAssemblyUnregisterResult> RetireModulePhysicalAsync(
        Assembly assembly, SharpLinkDynamicModule module, bool stopOwned, Task<Exception?> ready,
        TaskCompletionSource<Exception?> tracking)
    {
        var failure = await ready.ConfigureAwait(false);
        Task<SharpLinkAssemblyUnregisterResult> operation;
        lock (_registryGate)
        {
            if (!_unregisterOperations.TryGetValue(assembly, out var entry) || !ReferenceEquals(entry.Module, module))
                throw new InvalidOperationException("The exact retirement operation lost its registry owner.");
            operation = entry.Task;
            module.TryBeginDraining();
        }
        FrameworkTaskRegistration? receipt = null;
        Exception? trackingFailure = null;
        if (!stopOwned)
        {
            try { TrackOwnedRetirementTask(operation, module, ref receipt); }
            catch (Exception exception)
            { trackingFailure = exception; AddRetirementFailure(ref failure, exception); }
            finally { module.RetirementRegistration = receipt; }
        }
        tracking.TrySetResult(trackingFailure);
        try
        {
            await module.WaitForDrainAsync().ConfigureAwait(false);
            await ReleaseModuleAsync(assembly, module).ConfigureAwait(false);
        }
        catch (Exception exception) { AddRetirementFailure(ref failure, PhysicalTaskFailureException.GetRaw(exception)); }
        if (failure is not null) throw PhysicalTaskFailureException.From(failure);
        return new SharpLinkAssemblyUnregisterResult { ReferencesReleased = true };
    }

    private async Task<(Exception? Failure, bool SupervisorOwned)> ObserveRetirementExitAsync(
        Assembly assembly, SharpLinkDynamicModule module, Task<SharpLinkAssemblyUnregisterResult> operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch { /* Actual outcome has an explicit public/strict/lifecycle owner below. */ }
        Exception? failure = null;
        var receipt = module.RetirementRegistration;
        try { if (receipt is not null) receipt.Supervisor.CompleteAcceptedRegistration(receipt); }
        catch (Exception exception) { failure = exception; }
        try
        {
            lock (_registryGate)
            {
                // Receipt-null and unreleased failures stay discoverable until Stop actually
                // adopts them. Otherwise a retry could change State before an old Stop join.
                if (receipt is not null && failure is null && module.State == SharpLinkDynamicModuleState.Released &&
                    _unregisterOperations.TryGetValue(assembly, out var entry) &&
                    ReferenceEquals(entry.Task, operation) && ReferenceEquals(entry.Module, module))
                    _unregisterOperations.Remove(assembly);
            }
        }
        catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
        return (failure, receipt is not null);
    }

    private async ValueTask<SharpLinkAssemblyUnregisterResult> WaitForRetirementAsync(
        Task<SharpLinkAssemblyUnregisterResult> operation, SharpLinkDynamicModule module,
        Task<(Exception? Failure, bool SupervisorOwned)> observation, Task<Exception?> tracking,
        TimeSpan gracefulTimeout, CancellationToken cancellationToken)
    {
        var trackingFailure = cancellationToken.CanBeCanceled
            ? await tracking.WaitAsync(cancellationToken).ConfigureAwait(false) : await tracking.ConfigureAwait(false);
        var drain = module.WaitForDrainAsync();
        if (trackingFailure is null && !drain.IsCompleted)
        {
            if (!await SharpLinkTimer.WaitAsync(drain, gracefulTimeout, _runtimeContext.TimeProvider, cancellationToken).ConfigureAwait(false))
            {
                module.CancelRemainingCalls();
                await Task.Yield();
                if (!drain.IsCompleted)
                {
                    module.MarkDrainTimedOut();
                    return new SharpLinkAssemblyUnregisterResult
                    { ReferencesReleased = false, RemainingCalls = module.RemainingCalls, RemainingStreams = module.RemainingStreams };
                }
            }
        }
        Exception? failure = null;
        SharpLinkAssemblyUnregisterResult result = default!;
        try { result = cancellationToken.CanBeCanceled ? await operation.WaitAsync(cancellationToken).ConfigureAwait(false)
                : await operation.ConfigureAwait(false); }
        catch (Exception exception)
        {
            if (!operation.IsCompleted) throw;
            failure = PhysicalTaskFailureException.GetRaw(exception);
        }
        var observed = await observation.ConfigureAwait(false);
        if (observed.Failure is not null) AddRetirementFailure(ref failure, observed.Failure);
        ThrowRetirementFailure(failure);
        return result;
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
            Task<SharpLinkAssemblyUnregisterResult> operation;
            Task<(Exception? Failure, bool SupervisorOwned)> observation;
            TaskCompletionSource<Exception?>? ready = null;
            lock (_registryGate)
            {
                if (_unregisterOperations.TryGetValue(pair.Key, out var entry))
                {
                    if (!ReferenceEquals(entry.Module, pair.Value))
                        throw new InvalidOperationException("Stop cannot adopt an operation for another module generation.");
                    operation = entry.Task;
                }
                else if (pair.Value.State == SharpLinkDynamicModuleState.Released && pair.Value.RetirementOperation is { } completed)
                    operation = completed;
                else
                {
                    if (!_dynamicModules.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, pair.Value))
                        throw new InvalidOperationException("The exact Stop module disappeared before retirement election.");
                    pair.Value.TryBeginDraining();
                    operation = StartRetirementLocked(pair.Key, pair.Value, stopOwned: true, out ready);
                }
                observation = pair.Value.RetirementObservation!;
            }
            Exception? cancellationFailure = null;
            try { pair.Value.CancelRemainingCalls(); }
            catch (Exception exception) { cancellationFailure = exception; }
            ready?.TrySetResult(cancellationFailure);
            if (ready is null && cancellationFailure is not null) AddRetirementFailure(ref failure, cancellationFailure);
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
            if (_unregisterOperations.TryGetValue(assembly, out var entry) &&
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
