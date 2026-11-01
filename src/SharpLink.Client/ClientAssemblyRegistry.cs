using System.Reflection;

namespace SharpLink.Client;

/// <summary>Lock-free proxy lookup facade whose immutable snapshot is owned by <see cref="ClientAssemblyRegistry"/>.</summary>
internal sealed class ClientProxyLookup
{
    private ClientProxySnapshot _snapshot;
    internal ClientProxyLookup(ClientProxySnapshot snapshot) => _snapshot = snapshot;
    // Every current consumer is a cold registry operation under its mutation gate.
    internal FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> Capture() => _snapshot.Map;
    internal ClientProxySnapshot Acquire() => _snapshot.Retain();
    internal ClientProxySnapshot Current => _snapshot; // Caller holds registry gate.
    internal void Publish(ClientProxySnapshot snapshot)
    {
        var previous = _snapshot;
        _snapshot = snapshot;
        previous.Dispose();
    }
    internal void Clear() => Publish(new ClientProxySnapshot(
        FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration>.Empty));

}

/// <summary>
/// Owns the client-local generated contract/proxy registry and dynamic assembly lifecycle.
/// Registry mutations are serialized here; published proxy snapshots remain immutable and lock-free for readers.
/// </summary>
internal sealed partial class ClientAssemblyRegistry
{
    private const int MaximumDynamicModules = 4_096;

    private readonly SharpLinkRuntimeContext _runtimeContext;
    private IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> _staticManifests;
    private readonly ClientProxyLookup _proxyLookup;
    private readonly Func<SharpLinkConnectionState> _stateProvider;
    private readonly FrameworkTaskSupervisor _frameworkTasks;
    private readonly Lock _gate = new();
    private readonly Dictionary<Assembly, SharpLinkDynamicModule> _dynamicModules =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Assembly, (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module)> _unregisterOperations =
        new(ReferenceEqualityComparer.Instance);
    private long _generation;
    private bool _shutdownStarted;
    private Task _metadataPreparationsJoined = Task.CompletedTask;

    internal ClientAssemblyRegistry(
        SharpLinkRuntimeContext runtimeContext,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> staticManifests,
        ClientProxyLookup proxyLookup,
        Func<SharpLinkConnectionState> stateProvider,
        FrameworkTaskSupervisor frameworkTasks)
    {
        _runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        _staticManifests = staticManifests ?? throw new ArgumentNullException(nameof(staticManifests));
        _proxyLookup = proxyLookup ?? throw new ArgumentNullException(nameof(proxyLookup));
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        _frameworkTasks = frameworkTasks ?? throw new ArgumentNullException(nameof(frameworkTasks));
        foreach (var registration in _proxyLookup.Capture().Values)
            registration.LocalControl = CreateLocalControl(
                registration.Descriptor.ContractId, FindOwningManifest(registration).RpcAssemblyHash);
    }

    internal IReadOnlyDictionary<Assembly, SharpLinkDynamicModule> DynamicModules => _dynamicModules;




    internal ISharpLinkGeneratedAssemblyManifest FindOwningManifest(
        SharpLinkClient.ClientProxyRegistration registration)
    {
        if (registration.Module is { } module)
            return module.Manifest;

        var assembly = registration.Descriptor.ContractType.Assembly;
        for (var index = 0; index < _staticManifests.Count; index++)
        {
            var manifest = _staticManifests[index];
            if (ReferenceEquals(manifest.OwnerAssembly, assembly))
                return manifest;
        }

        throw new InvalidOperationException(
            $"No registered manifest owns RPC contract assembly '{assembly.FullName}'.");
    }

    /// <summary>
    /// Establishes the terminal registry barrier used by Client stop. Once this method returns, no
    /// registration/replacement commit can publish a new module generation.
    /// </summary>
    internal void BeginShutdown()
    {
        ClientLocalContractControl[] retired;
        lock (_gate)
        {
            retired = CaptureLocalControlsLocked();
            _shutdownStarted = true;
            _metadataPreparationsJoined = _runtimeContext.Domain.SealAndJoinAsync();
            RetireLocalControls(retired);
        }
        NotifyLocalRetirement(retired);
    }

    internal SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
    {
        using var preparation = _runtimeContext.Domain.BeginPreparation();
        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(assembly, preparation, out var manifest);
        return loaded.Succeeded ? RegisterPreparedAssembly(manifest!) : loaded;
    }

    internal SharpLinkAssemblyRegistrationResult RegisterPreparedAssembly(
        PreparedGeneratedManifest manifest,
        Func<Func<SharpLinkAssemblyRegistrationResult>, SharpLinkAssemblyRegistrationResult>? publication = null)
    {
        using var preparation = _runtimeContext.Domain.BeginPreparation();
        PreparedGeneratedManifest.Capture(manifest, preparation);
        var assembly = manifest.OwnerAssembly;
        RpcGeneratedManifestRegistration? codecRegistration = null;
        ClientProxySnapshot? candidateSnapshot = null;
        var published = false;
        SharpLinkAssemblyRegistrationError? rollbackError = null;
        Exception? rollbackException = null;
        SharpLinkRuntimeContext.CapturedGeneratedCodecs? codecSource = null;
        try
        {
            codecSource = _runtimeContext.CaptureGeneratedCodecSnapshot();
            long generation;
            ClientProxySnapshot current;
            SharpLinkDynamicModule[] modules;
            lock (_gate)
            {
                if (RejectMutationLocked(out var state))
                    return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                        $"Client state '{state}' does not accept runtime assembly registration.", assembly);
                if (IsAssemblyRegistered(assembly))
                    return Failure(SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
                        "The same Assembly object is already registered on this client.", assembly);
                if (_dynamicModules.Count >= MaximumDynamicModules)
                    return Failure(SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
                        $"The client runtime module limit of {MaximumDynamicModules} has been reached.", assembly);
                preparation.ReserveCollection(_dynamicModules.Count);
                modules = [.. _dynamicModules.Values];
                generation = _generation;
                current = _proxyLookup.Acquire();
            }
            using (current)
            {
                codecRegistration = _runtimeContext.PrepareGeneratedManifest(manifest);
                var module = new SharpLinkDynamicModule(assembly, manifest, codecRegistration);
                var candidate = BuildRegistrationCandidate(manifest, module, current.Map, current,
                    modules, codecSource.Registrations, preparation, out var error);
                if (error is not null)
                {
                    rollbackError = error;
                    return SharpLinkAssemblyRegistrationResult.Failure(error);
                }
                candidateSnapshot = candidate.Proxies;
                using var codecPublication = _runtimeContext.PrepareGeneratedCodecPublication(
                    candidate.Codecs, codecRegistration, adoptPendingRegistration: true, sourceSnapshot: codecSource);
                SharpLinkAssemblyRegistrationResult Commit()
                {
                    lock (_gate)
                    {
                        if (generation != _generation || RejectMutationLocked(out _) || IsAssemblyRegistered(assembly) ||
                            !_runtimeContext.IsCurrentGeneratedCodecPublication(codecPublication))
                            return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                                "The registry changed while this exact candidate was prepared; no callbacks were replayed.", assembly);
                        _runtimeContext.CommitGeneratedCodecPublication(codecPublication);
                        _dynamicModules.Add(assembly, module);
                        _proxyLookup.Publish(candidateSnapshot);
                        _generation++;
                        published = true;
                        return SharpLinkAssemblyRegistrationResult.Success();
                    }
                }
                return publication is null ? Commit() : publication(Commit);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            rollbackException = exception;
            rollbackError = CreateError(exception is ColdMetadataException cold ? cold.Code :
                SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                $"Assembly registration failed transactionally: {exception.GetType().Name}: {exception.Message}", assembly);
            return SharpLinkAssemblyRegistrationResult.Failure(rollbackError);
        }
        finally
        {
            try
            {
                if (!published)
                {
                    candidateSnapshot?.Dispose();
                    try { codecRegistration?.Dispose(); }
                    catch (Exception cleanupException)
                    { ThrowAfterAssemblyRollback(rollbackError, rollbackException, cleanupException); }
                }
            }
            finally { codecSource?.Dispose(); }
        }
    }

    internal ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
        Assembly oldAssembly,
        Assembly newAssembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default,
        PreparedGeneratedManifest? preparedManifest = null)
    {
        ArgumentNullException.ThrowIfNull(oldAssembly);
        ArgumentNullException.ThrowIfNull(newAssembly);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        if (ReferenceEquals(oldAssembly, newAssembly))
        {
            return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(CreateError(
                SharpLinkAssemblyRegistrationErrorCode.InvalidArgument,
                "The old and new Assembly objects must be different.",
                newAssembly)));
        }

        using var preparation = _runtimeContext.Domain.BeginPreparation();
        PreparedGeneratedManifest? manifest;
        if (preparedManifest is not null)
            manifest = PreparedGeneratedManifest.Capture(preparedManifest, preparation, newAssembly);
        else
        {
            var loaded = SharpLinkAssemblyManifestLoader.TryLoad(newAssembly, preparation, out manifest);
            if (!loaded.Succeeded)
                return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(loaded.Error!));
        }

        using var codecSource = _runtimeContext.CaptureGeneratedCodecSnapshot();
        ClientLocalContractControl[] retiredControls = [];
        Task<SharpLinkAssemblyUnregisterResult>? drainOperation = null;
        Task<(Exception? Failure, bool SupervisorOwned)>? drainObservation = null;
        TaskCompletionSource<Exception?>? retirementReady = null;
        Task<Exception?>? retirementTracking = null;
        Exception? publicationFailure = null;
        SharpLinkDynamicModule? oldModule = null;
        SharpLinkDynamicModule? newModule = null;
        ClientProxySnapshot? candidateSnapshot = null;
        ClientProxySnapshot? retainedSourceOwners = null;
        RpcGeneratedManifestRegistration? codecRegistration = null;
        SharpLinkAssemblyRegistrationError? rollbackError = null;
        Exception? rollbackException = null;
        var published = false;
        try
        {
            while (true)
            {
                long generation;
                FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> retainedProxies;
                SharpLinkDynamicModule[] currentModules;
                IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> retainedCodecs;
                lock (_gate)
                {
                    if (RejectMutationLocked(out var state))
                    {
                        return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(CreateError(
                            SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                            $"Client state '{state}' does not accept runtime assembly replacement.",
                            newAssembly)));
                    }
                    if (!_dynamicModules.TryGetValue(oldAssembly, out oldModule) ||
                        oldModule.State != SharpLinkDynamicModuleState.Running ||
                        _unregisterOperations.ContainsKey(oldAssembly))
                    {
                        return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(CreateError(
                            SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                            "The old Assembly object does not own a running runtime registration.",
                            newAssembly)));
                    }
                    if (IsAssemblyRegistered(newAssembly))
                    {
                        return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(CreateError(
                            SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
                            "The new Assembly object is already registered on this client.",
                            newAssembly)));
                    }
                    if (_dynamicModules.Count >= MaximumDynamicModules)
                    {
                        return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(CreateError(
                            SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
                            $"The client runtime module limit of {MaximumDynamicModules} has been reached; wait for a draining replacement to finish.",
                            newAssembly)));
                    }
                    var replacementDependencyError = ValidateReplacementDependants(oldModule, manifest!);
                    if (replacementDependencyError is not null)
                        return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(replacementDependencyError));

                    generation = _generation;
                    retainedSourceOwners = _proxyLookup.Acquire();
                    preparation.ReserveCollection(_dynamicModules.Count);
                    preparation.ReserveCollection(_proxyLookup.Capture().Count);
                    preparation.ReserveCollection(_proxyLookup.Capture().Count);
                    currentModules = _dynamicModules.Values
                        .Where(module => !ReferenceEquals(module, oldModule))
                        .ToArray();
                    retainedProxies = _proxyLookup.Capture()
                        .Where(pair => !ReferenceEquals(pair.Value.Module, oldModule))
                        .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                        .ToFrozenDictionary();
                    retainedCodecs = CreateCodecSnapshotWithout(oldModule, codecSource, preparation);
                }

                codecRegistration = _runtimeContext.PrepareGeneratedManifest(manifest!);
                newModule = new SharpLinkDynamicModule(newAssembly, manifest!, codecRegistration);
                var candidate = BuildRegistrationCandidate(
                    manifest!, newModule, retainedProxies, retainedSourceOwners!, currentModules, retainedCodecs, preparation, out var error);
                if (error is not null)
                {
                    rollbackError = error;
                    return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(error));
                }

                candidateSnapshot = candidate.Proxies;
                using var codecPublication = _runtimeContext.PrepareGeneratedCodecPublication(
                    candidate.Codecs, codecRegistration, adoptPendingRegistration: true, sourceSnapshot: codecSource);
                lock (_gate)
                {
                    if (generation != _generation || !_runtimeContext.IsCurrentGeneratedCodecPublication(codecPublication))
                    {
                        return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(CreateError(
                            SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                            "Registry changed while the exact replacement was prepared; callbacks were not replayed.", newAssembly)));
                    }
                    else
                    {
                        if (RejectMutationLocked(out var state))
                        {
                            rollbackError = CreateError(
                                SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                                $"Client state '{state}' does not accept runtime assembly replacement.",
                                newAssembly);
                            return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(rollbackError));
                        }
                        if (!_dynamicModules.TryGetValue(oldAssembly, out var currentOldModule) ||
                            !ReferenceEquals(currentOldModule, oldModule) ||
                            oldModule.State != SharpLinkDynamicModuleState.Running ||
                            _unregisterOperations.ContainsKey(oldAssembly))
                        {
                            rollbackError = CreateError(
                                SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                                "The old Assembly object changed while the replacement candidate was being prepared.",
                                newAssembly);
                            return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(rollbackError));
                        }
                        if (!ReplacementCodecsRemainCurrent(oldModule, retainedCodecs, codecSource))
                            return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(CreateError(
                                SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                                "A replacement codec owner started retiring while callbacks ran; callbacks were not replayed.", newAssembly)));
                        retiredControls = CaptureLocalControlsLocked(oldModule);
                        _runtimeContext.CommitGeneratedCodecPublication(codecPublication);
                        _dynamicModules.Add(newAssembly, newModule);
                        RetireLocalControls(retiredControls);
                        oldModule.TryBeginDraining();
                        published = true; // Runtime/new-module adoption is committed; never roll it back as a draft.
                        drainOperation = StartRetirementLocked(oldAssembly, oldModule, out retirementReady);
                        drainObservation = oldModule.RetirementObservation;
                        retirementTracking = oldModule.RetirementTracking;
                        _generation++;
                        _proxyLookup.Publish(candidate.Proxies);
                        break;
                    }
                }

            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (published) AddRetirementFailure(ref publicationFailure, exception);
            else
            {
                rollbackException = exception;
                rollbackError = CreateError(
                    exception is ColdMetadataException cold ? cold.Code : SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Assembly replacement failed transactionally: {exception.GetType().Name}: {exception.Message}",
                    newAssembly);
                return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(rollbackError));
            }
        }
        finally
        {
            if (published)
            {
                // These are all post-commit obligations of the physical retirement owner.
                // The outer using declarations are idempotent after explicit disposal here.
                AttemptRetirementCleanup(retainedSourceOwners, ref publicationFailure);
                AttemptRetirementCleanup(codecSource, ref publicationFailure);
                AttemptRetirementCleanup(preparation, ref publicationFailure);
                try { NotifyLocalRetirement(retiredControls); }
                catch (Exception exception) { AddRetirementFailure(ref publicationFailure, exception); }
                retirementReady!.TrySetResult(publicationFailure);
            }
            else
            {
                Exception? cleanupFailure = null;
                AttemptRetirementCleanup(retainedSourceOwners, ref cleanupFailure);
                AttemptRetirementCleanup(candidateSnapshot, ref cleanupFailure);
                AttemptRetirementCleanup(codecRegistration, ref cleanupFailure);
                AttemptRetirementCleanup(codecSource, ref cleanupFailure);
                AttemptRetirementCleanup(preparation, ref cleanupFailure);
                if (cleanupFailure is not null)
                    ThrowAfterAssemblyRollback(rollbackError, rollbackException, cleanupFailure);
            }
        }

        return WaitForReplacementRetirementAsync(drainOperation!, oldModule!, drainObservation, retirementTracking, gracefulTimeout, cancellationToken);
    }

    internal ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
        Assembly assembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        ColdMetadataPreparation.ThrowIfCallbackReentry(_runtimeContext.Domain);
        Task<SharpLinkAssemblyUnregisterResult> operation;
        SharpLinkDynamicModule? module;
        Task<(Exception? Failure, bool SupervisorOwned)>? observation;
        Task<Exception?>? tracking;
        TaskCompletionSource<Exception?>? retirementReady = null;
        ClientLocalContractControl[] retiredControls = [];
        lock (_gate)
        {
            if (_unregisterOperations.TryGetValue(assembly, out var existing))
            {
                if (_dynamicModules.TryGetValue(assembly, out var active) && !ReferenceEquals(active, existing.Module))
                    throw new InvalidOperationException("An unregister operation belongs to another module generation.");
                operation = existing.Task;
                module = existing.Module;
            }
            else
            {
                if (!_dynamicModules.TryGetValue(assembly, out module))
                    return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
                EnsureNoDynamicDependants(module);
                retiredControls = CaptureLocalControlsLocked(module);
                RetireLocalControls(retiredControls);
                module.TryBeginDraining();
                operation = StartRetirementLocked(assembly, module, out retirementReady);
            }
            observation = module?.RetirementObservation;
            tracking = module?.RetirementTracking;
        }
        Exception? notificationFailure = null;
        try { NotifyLocalRetirement(retiredControls); }
        catch (Exception exception) { notificationFailure = exception; }
        finally { retirementReady?.TrySetResult(notificationFailure); }
        return WaitForRetirementAsync(operation, module, observation, tracking, gracefulTimeout, cancellationToken);
    }

    internal async ValueTask<Exception[]> DrainForShutdownAsync()
    {
        await _metadataPreparationsJoined.ConfigureAwait(false);
        List<Exception>? failures = null;
        var modules = GetDynamicModulesForShutdown(out var terminal);
        foreach (var value in terminal)
        {
            Exception? original = null;
            try { await value.Task.ConfigureAwait(false); }
            catch (Exception exception) { original = PhysicalTaskFailureException.GetRaw(exception); }
            var observed = await value.Module.RetirementObservation!.ConfigureAwait(false);
            if (original is not null && !observed.SupervisorOwned) (failures ??= []).Add(original);
            if (observed.Failure is not null) (failures ??= []).Add(observed.Failure);
            lock (_gate)
            {
                // Released modules no longer expose Assembly. Remove by the exact task entry,
                // after its late infrastructure outcome has transferred into this Stop owner.
                var key = _unregisterOperations.FirstOrDefault(pair => ReferenceEquals(pair.Value.Task, value.Task)).Key;
                if (key is not null) _unregisterOperations.Remove(key);
            }
        }
        foreach (var pair in modules)
        {
            Task<SharpLinkAssemblyUnregisterResult> operation;
            Task<(Exception? Failure, bool SupervisorOwned)>? observation;
            TaskCompletionSource<Exception?>? retirementReady = null;
            lock (_gate)
            {
                pair.Value.TryBeginDraining();
                if (_unregisterOperations.TryGetValue(pair.Key, out var existing))
                {
                    if (!ReferenceEquals(existing.Module, pair.Value))
                        throw new InvalidOperationException("Shutdown retirement selected a different module generation.");
                    operation = existing.Task;
                }
                else operation = StartRetirementLocked(pair.Key, pair.Value, out retirementReady);
                observation = pair.Value.RetirementObservation;
            }
            retirementReady?.TrySetResult(null);
            pair.Value.CancelRemainingCalls();
            Exception? operationFailure = null;
            try { await operation.ConfigureAwait(false); }
            catch (Exception exception) { operationFailure = PhysicalTaskFailureException.GetRaw(exception); }
            var observed = observation is null ? (Failure: (Exception?)null, SupervisorOwned: false)
                : await observation.ConfigureAwait(false);
            if (operationFailure is not null && !observed.SupervisorOwned)
                (failures ??= []).Add(operationFailure);
            if (observed.Failure is not null) (failures ??= []).Add(observed.Failure);
            bool released;
            lock (_gate)
            {
                released = pair.Value.State == SharpLinkDynamicModuleState.Released;
                // Definitive adoption into this retained Stop owner, not a shutdown-state guess.
                if (_unregisterOperations.TryGetValue(pair.Key, out var retained) &&
                    ReferenceEquals(retained.Task, operation) && ReferenceEquals(retained.Module, pair.Value))
                    _unregisterOperations.Remove(pair.Key);
            }
            // Completed faulted is not released. Do not advance to B or Runtime disposal.
            if (!released)
            {
                // This is the existing lifecycle reporter, not a second copy of the task fault.
                try { await _frameworkTasks.DrainAsync().ConfigureAwait(false); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
                if (failures is { Count: 1 }) ThrowRetirementFailure(failures[0]);
                if (failures is not null) throw new AggregateException(failures);
                throw new InvalidOperationException("Dynamic module cleanup ended without physical release; dependent ownership is retained.");
            }
        }
        return failures?.ToArray() ?? [];
    }

    internal void ReleaseMetadataAfterDrain()
    {
        lock (_gate)
        {
            _proxyLookup.Clear();
            _staticManifests = [];
        }
    }

    internal bool IsDynamicAssemblyRegistered(Assembly assembly)
    {
        lock (_gate)
            return _dynamicModules.ContainsKey(assembly);
    }

    internal static ClientProxySnapshot BuildStaticProxySnapshot(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests,
        SharpLinkRuntimeContext runtimeContext)
    {
        using var preparation = runtimeContext.Domain.BeginPreparation();
        var count = 0;
        foreach (var manifest in manifests) count = checked(count + manifest.Contracts.Count);
        for (var copy = 0; copy < 3; copy++) preparation.ReserveCollection(count);
        var registrations = new Dictionary<Type, SharpLinkClient.ClientProxyRegistration>(count);
        var contractIds = new Dictionary<long, ISharpLinkGeneratedAssemblyManifest>(count);
        for (var manifestIndex = 0; manifestIndex < manifests.Count; manifestIndex++)
        {
            var manifest = manifests[manifestIndex];
            ValidateStaticManifestCompatibility(manifest);
            for (var index = 0; index < manifest.Contracts.Count; index++)
            {
                var contract = manifest.Contracts[index];
                if (contractIds.TryGetValue(contract.ContractId, out var existing))
                {
                    throw new InvalidOperationException(
                        $"Contract conflict for '{contract.ContractName}' ({contract.ContractId}). " +
                        $"Incoming Assembly='{manifest.OwnerAssembly.FullName}', " +
                        $"ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(manifest.OwnerAssembly)}', " +
                        $"Fingerprint='{contract.Fingerprint}'; Existing Assembly='{existing.OwnerAssembly.FullName}', " +
                        $"ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(existing.OwnerAssembly)}'.");
                }
                contractIds.Add(contract.ContractId, manifest);
                registrations.Add(contract.ContractType, new SharpLinkClient.ClientProxyRegistration(
                    contract,
                    null,
                    RpcGeneratedCodecResolver.GetProvider(runtimeContext, contract.ContractType)));
            }
        }
        return ClientProxySnapshot.Create(registrations.ToFrozenDictionary(), preparation, staticManifests: manifests);
    }

    internal static void ValidateStaticManifestCompatibility(
        ISharpLinkGeneratedAssemblyManifest manifest)
        => SharpLinkGeneratedManifestCompatibility.ThrowIfIncompatible(manifest);

    private bool RejectMutation(out SharpLinkConnectionState state)
    {
        state = _stateProvider();
        return Volatile.Read(ref _shutdownStarted) ||
               state is SharpLinkConnectionState.Draining or
                   SharpLinkConnectionState.Stopped or
                   SharpLinkConnectionState.Faulted;
    }

    private bool RejectMutationLocked(out SharpLinkConnectionState state)
    {
        state = _stateProvider();
        return _shutdownStarted ||
               state is SharpLinkConnectionState.Draining or
                   SharpLinkConnectionState.Stopped or
                   SharpLinkConnectionState.Faulted;
    }

    // Caller holds _gate. The cold readiness gate is released OUTSIDE the registry gate
    // after publication/notification cleanup; the dictionary roots the actual suspended worker.
    private Task<SharpLinkAssemblyUnregisterResult> StartRetirementLocked(
        Assembly assembly, SharpLinkDynamicModule module, out TaskCompletionSource<Exception?>? ready)
    {
        ready = null;
        if (module.State == SharpLinkDynamicModuleState.Released && module.RetirementOperation is { } completed)
            return completed;
        ready = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        module.RetirementRegistration = null;
        var tracking = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        module.RetirementTracking = tracking.Task;
        var operation = RetireModulePhysicalAsync(assembly, module, ready.Task, tracking);
        module.RetirementOperation = operation;
        _unregisterOperations.Add(assembly, (operation, module));
        module.RetirementObservation = ObserveRetirementExitAsync(assembly, module, operation);
        return operation;
    }

    private async Task<SharpLinkAssemblyUnregisterResult> RetireModulePhysicalAsync(
        Assembly assembly, SharpLinkDynamicModule module, Task<Exception?> ready,
        TaskCompletionSource<Exception?> tracking)
    {
        var failure = await ready.ConfigureAwait(false);
        Task<SharpLinkAssemblyUnregisterResult> operation;
        bool supervise;
        lock (_gate)
        {
            operation = _unregisterOperations[assembly].Task;
            supervise = !_shutdownStarted;
        }
        FrameworkTaskRegistration? receipt = null;
        Exception? trackingFailure = null;
        if (supervise)
        {
            try { _frameworkTasks.TrackOwnedStrictPhysicalTail(operation, "DynamicAssemblyPhysicalRetirement", module, ref receipt); }
            catch (Exception exception)
            {
                trackingFailure = exception;
                AddRetirementFailure(ref failure, exception);
            }
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
        // Actual task remains in the dictionary through this exact terminal receipt handling.
        try { await operation.ConfigureAwait(false); }
        catch { /* Raw operation outcome remains with its explicit caller/supervisor owner. */ }
        Exception? failure = null;
        var receipt = module.RetirementRegistration;
        try
        {
            if (receipt is not null)
                receipt.Supervisor.CompleteAcceptedRegistration(receipt);
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            lock (_gate)
                if (receipt is not null && failure is null &&
                    _unregisterOperations.TryGetValue(assembly, out var current) &&
                    ReferenceEquals(current.Task, operation) && ReferenceEquals(current.Module, module))
                    _unregisterOperations.Remove(assembly);
                // Receipt-null is NOT proof that Stop has adopted this outcome. Retain the exact
                // task/module until actual Stop joins it, even after physical removal or success.
                // Independent observer failures follow the same retained transfer rule.
        }
        return (failure, receipt is not null);
    }

    private async ValueTask<SharpLinkAssemblyUnregisterResult> WaitForRetirementAsync(
        Task<SharpLinkAssemblyUnregisterResult> operation, SharpLinkDynamicModule? module,
        Task<(Exception? Failure, bool SupervisorOwned)>? observation, Task<Exception?>? tracking,
        TimeSpan gracefulTimeout, CancellationToken cancellationToken)
    {
        var trackingFailure = tracking is null ? null : cancellationToken.CanBeCanceled
            ? await tracking.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await tracking.ConfigureAwait(false);
        // A failed registration is joined by its actual caller/physical lifecycle owner;
        // it is never mislabeled as a successfully detached zero-grace operation.
        if (trackingFailure is null && module is not null && !module.WaitForDrainAsync().IsCompleted)
        {
            var drained = await SharpLinkTimer.WaitAsync(module.WaitForDrainAsync(), gracefulTimeout,
                _runtimeContext.TimeProvider, cancellationToken).ConfigureAwait(false);
            if (!drained)
            {
                module.CancelRemainingCalls();
                await Task.Yield();
                if (!module.WaitForDrainAsync().IsCompleted)
                {
                    module.MarkDrainTimedOut();
                    return new SharpLinkAssemblyUnregisterResult
                    {
                        ReferencesReleased = false,
                        RemainingCalls = module.RemainingCalls,
                        RemainingStreams = module.RemainingStreams
                    };
                }
            }
        }
        Exception? failure = null;
        SharpLinkAssemblyUnregisterResult result = default!;
        try
        {
            result = cancellationToken.CanBeCanceled
                ? await operation.WaitAsync(cancellationToken).ConfigureAwait(false)
                : await operation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Cancellation of this waiter must not turn into an unbounded observer join.
            if (!operation.IsCompleted) throw;
            failure = PhysicalTaskFailureException.GetRaw(exception);
        }
        if (observation is not null)
        {
            var observed = await observation.ConfigureAwait(false);
            if (observed.Failure is not null) AddRetirementFailure(ref failure, observed.Failure);
        }
        ThrowRetirementFailure(failure);
        return result;
    }

    private async ValueTask<SharpLinkAssemblyReplacementResult> WaitForReplacementRetirementAsync(
        Task<SharpLinkAssemblyUnregisterResult> operation, SharpLinkDynamicModule module,
        Task<(Exception? Failure, bool SupervisorOwned)>? observation, Task<Exception?>? tracking,
        TimeSpan gracefulTimeout, CancellationToken cancellationToken)
        => SharpLinkAssemblyReplacementResult.Published(
            await WaitForRetirementAsync(operation, module, observation, tracking, gracefulTimeout, cancellationToken).ConfigureAwait(false));

    private async Task ReleaseModuleAsync(Assembly assembly, SharpLinkDynamicModule module)
    {
        Task<Exception?>? existing;
        RpcGeneratedManifestRegistration? codecRegistration = null;
        IDisposable? metadata = null;
        lock (_gate)
        {
            existing = module.CleanupCompletion?.Task;
            if (existing is null)
            {
                if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module))
                    return;
                codecRegistration = module.CodecRegistration;
                metadata = codecRegistration.MetadataLifetime.Retain();
            }
        }
        if (existing is not null)
        {
            ThrowRetirementFailure(await existing.ConfigureAwait(false));
            return;
        }

        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var claimed = false;
        try
        {
            while (!claimed && existing is null)
            {
                ColdMetadataPreparation? storage = null;
                SharpLinkRuntimeContext.CapturedGeneratedCodecs? codecSource = null;
                SharpLinkRuntimeContext.PreparedGeneratedCodecPublication? publication = null;
                ClientProxySnapshot? nextSnapshot = null;
                var published = false;
                var retry = false;
                try
                {
                    // Owned cleanup storage, not new management admission after shutdown seal.
                    storage = _runtimeContext.CreateMetadataStorage();
                    codecSource = _runtimeContext.CaptureGeneratedCodecSnapshot();
                    long generation;
                    Dictionary<Type, RpcGeneratedCodecRegistration> nextFactories;
                    lock (_gate)
                    {
                        existing = module.CleanupCompletion?.Task;
                        if (existing is not null) continue;
                        if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module))
                            throw new InvalidOperationException("The unclaimed retirement module disappeared.");
                        generation = _generation;
                        var currentSnapshot = _proxyLookup.Current;
                        storage.ReserveCollection(currentSnapshot.Map.Count);
                        storage.ReserveCollection(currentSnapshot.Map.Count);
                        var nextProxies = new Dictionary<Type, SharpLinkClient.ClientProxyRegistration>(currentSnapshot.Map.Count);
                        foreach (var pair in currentSnapshot.Map)
                            if (!ReferenceEquals(pair.Value.Module, module)) nextProxies.Add(pair.Key, pair.Value);
                        nextSnapshot = ClientProxySnapshot.Create(nextProxies.ToFrozenDictionary(), storage, currentSnapshot);
                        var factories = codecSource.Registrations;
                        storage.ReserveCollection(factories.Count);
                        nextFactories = new Dictionary<Type, RpcGeneratedCodecRegistration>(factories.Count);
                        foreach (var pair in factories) nextFactories.Add(pair.Key, pair.Value);
                        foreach (var codecType in codecRegistration!.Codecs.Keys)
                        {
                            var replacement = FindReplacementCodec(codecType, module, codecSource);
                            if (replacement is null) nextFactories.Remove(codecType);
                            else nextFactories[codecType] = replacement;
                        }
                    }
                    publication = _runtimeContext.PrepareGeneratedCodecPublication(nextFactories, sourceSnapshot: codecSource);
                    lock (_gate)
                    {
                        existing = module.CleanupCompletion?.Task;
                        if (existing is not null) continue;
                        if (generation != _generation || !_runtimeContext.IsCurrentGeneratedCodecPublication(publication) ||
                            !ReplacementCodecsRemainCurrent(module, nextFactories, codecSource))
                        {
                            retry = true;
                        }
                        else
                        {
                            _runtimeContext.CommitGeneratedCodecPublication(publication);
                            module.CleanupCompletion = completion;
                            claimed = true;
                            _generation++;
                            // Publish swaps before releasing its previous snapshot. A refund failure
                            // is a winner-owned cleanup error, never permission to discard the new map.
                            published = true;
                            _proxyLookup.Publish(nextSnapshot);
                        }
                    }
                }
                catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
                finally
                {
                    AttemptRetirementCleanup(publication, ref failure);
                    if (!published) AttemptRetirementCleanup(nextSnapshot, ref failure);
                    AttemptRetirementCleanup(codecSource, ref failure);
                    AttemptRetirementCleanup(storage, ref failure);
                }
                if (failure is not null || (!claimed && existing is null && !retry)) break;
            }
            if (claimed)
            {
                try { _runtimeContext.ReleaseGeneratedManifest(codecRegistration!); }
                catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
                // Keep A dependency-visible throughout every actual adapter/codec callback.
                lock (_gate)
                {
                    if (_dynamicModules.TryGetValue(assembly, out var current) && ReferenceEquals(current, module))
                    {
                        _dynamicModules.Remove(assembly);
                        _generation++;
                    }
                    module.MarkReleased();
                }
            }
        }
        catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
        finally
        {
            // Root clearing precedes the last standing metadata refund, outside the registry gate.
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

    private bool ReplacementCodecsRemainCurrent(
        SharpLinkDynamicModule removedModule,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> expected,
        SharpLinkRuntimeContext.CapturedGeneratedCodecs codecSource)
    {
        // Caller holds the host gate. Logical draining need not change _generation.
        foreach (var type in removedModule.CodecRegistration.Codecs.Keys)
        {
            expected.TryGetValue(type, out var selected);
            if (!ReferenceEquals(selected, FindReplacementCodec(type, removedModule, codecSource)))
                return false;
        }
        return true;
    }

    private static void AttemptRetirementCleanup(IDisposable? resource, ref Exception? failure)
    {
        try { resource?.Dispose(); }
        catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
    }

    private static void AddRetirementFailure(ref Exception? failure, Exception exception)
        => failure = failure is null ? exception : new AggregateException(failure, exception);

    private static void ThrowRetirementFailure(Exception? failure)
    {
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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
}
