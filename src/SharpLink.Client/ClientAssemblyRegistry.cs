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
    private readonly Action<Task, string, TaskObservationMode> _trackFrameworkTask;
    private readonly Lock _gate = new();
    private readonly Dictionary<Assembly, SharpLinkDynamicModule> _dynamicModules =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Assembly, Task<SharpLinkAssemblyUnregisterResult>> _unregisterOperations =
        new(ReferenceEqualityComparer.Instance);
    private long _generation;
    private bool _shutdownStarted;
    private Task _metadataPreparationsJoined = Task.CompletedTask;

    internal ClientAssemblyRegistry(
        SharpLinkRuntimeContext runtimeContext,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> staticManifests,
        ClientProxyLookup proxyLookup,
        Func<SharpLinkConnectionState> stateProvider,
        Action<Task, string, TaskObservationMode> trackFrameworkTask)
    {
        _runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        _staticManifests = staticManifests ?? throw new ArgumentNullException(nameof(staticManifests));
        _proxyLookup = proxyLookup ?? throw new ArgumentNullException(nameof(proxyLookup));
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        _trackFrameworkTask = trackFrameworkTask ?? throw new ArgumentNullException(nameof(trackFrameworkTask));
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
        try
        {
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
                using var codecSource = _runtimeContext.CaptureGeneratedCodecSnapshot();
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
                        if (generation != _generation || RejectMutationLocked(out _) ||
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
            if (!published)
            {
                candidateSnapshot?.Dispose();
                try { codecRegistration?.Dispose(); }
                catch (Exception cleanupException)
                { ThrowAfterAssemblyRollback(rollbackError, rollbackException, cleanupException); }
            }
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
        TaskCompletionSource<SharpLinkAssemblyUnregisterResult>? drainCompletion = null;
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
                    retainedCodecs = CreateCodecSnapshotWithout(oldModule, codecSource.Registrations, preparation);
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
                        drainCompletion = new TaskCompletionSource<SharpLinkAssemblyUnregisterResult>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        drainOperation = drainCompletion.Task;
                        retiredControls = CaptureLocalControlsLocked(oldModule);
                        _runtimeContext.CommitGeneratedCodecPublication(codecPublication);
                        _dynamicModules.Add(newAssembly, newModule);
                        _unregisterOperations.Add(oldAssembly, drainOperation);
                        RetireLocalControls(retiredControls);
                        oldModule.TryBeginDraining();
                        _proxyLookup.Publish(candidate.Proxies);
                        _generation++;
                        published = true;
                        break;
                    }
                }

            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            rollbackException = exception;
            rollbackError = CreateError(
                exception is ColdMetadataException cold ? cold.Code : SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                $"Assembly replacement failed transactionally: {exception.GetType().Name}: {exception.Message}",
                newAssembly);
            return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(rollbackError));
        }
        finally
        {
            retainedSourceOwners?.Dispose();
            if (!published)
            {
                candidateSnapshot?.Dispose();
                try
                {
                    codecRegistration?.Dispose();
                }
                catch (Exception cleanupException)
                {
                    ThrowAfterAssemblyRollback(rollbackError, rollbackException, cleanupException);
                }
            }
        }

        _trackFrameworkTask(
            drainOperation!,
            "DynamicAssemblyReplacementDrain",
            TaskObservationMode.ExternallyObserved);
        NotifyLocalRetirement(retiredControls);
        _ = CompleteUnregisterOperationAsync(oldAssembly, oldModule!, gracefulTimeout, drainCompletion!);
        return WaitForReplacementAsync(drainOperation!, cancellationToken);
    }

    internal ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
        Assembly assembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        Task<SharpLinkAssemblyUnregisterResult> operation;
        ClientLocalContractControl[] retiredControls;
        lock (_gate)
        {
            if (_unregisterOperations.TryGetValue(assembly, out operation!))
                return WaitForUnregisterAsync(operation, cancellationToken);
            if (!_dynamicModules.TryGetValue(assembly, out var module))
                return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
            EnsureNoDynamicDependants(module);
            var completion = new TaskCompletionSource<SharpLinkAssemblyUnregisterResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            operation = completion.Task;
            retiredControls = CaptureLocalControlsLocked(module);
            _unregisterOperations.Add(assembly, operation);
            RetireLocalControls(retiredControls);
            module.TryBeginDraining();
            _ = CompleteUnregisterOperationAsync(assembly, module, gracefulTimeout, completion);
            if (_stateProvider() != SharpLinkConnectionState.Draining)
            {
                _trackFrameworkTask(
                    operation,
                    "DynamicAssemblyUnregister",
                    TaskObservationMode.ExternallyObserved);
            }
        }
        NotifyLocalRetirement(retiredControls);
        return WaitForUnregisterAsync(operation, cancellationToken);
    }

    internal async ValueTask<Exception[]> DrainForShutdownAsync()
    {
        await _metadataPreparationsJoined.ConfigureAwait(false);
        var dynamicAssemblies = GetDynamicAssembliesForShutdown();
        List<Exception>? failures = null;
        for (var index = 0; index < dynamicAssemblies.Length; index++)
        {
            try
            {
                await UnregisterAssemblyAsync(dynamicAssemblies[index], TimeSpan.Zero).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        return failures?.ToArray() ?? [];
    }

    // Called only after Client framework/physical cleanup has joined. No published map
    // or static-manifest root remains when Runtime releases its retained metadata bundle.
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
                    _trackFrameworkTask(
                        CompleteTimedOutUnregisterAsync(assembly, module, drainTask),
                        "DynamicAssemblyTimedOutUnregisterCleanup",
                        TaskObservationMode.FrameworkOwned);
                    return new SharpLinkAssemblyUnregisterResult
                    {
                        ReferencesReleased = false,
                        RemainingCalls = module.RemainingCalls,
                        RemainingStreams = module.RemainingStreams
                    };
                }
            }
        }
        ReleaseModule(assembly, module);
        return new SharpLinkAssemblyUnregisterResult { ReferencesReleased = true };
    }

    private async Task CompleteUnregisterOperationAsync(
        Assembly assembly,
        SharpLinkDynamicModule module,
        TimeSpan gracefulTimeout,
        TaskCompletionSource<SharpLinkAssemblyUnregisterResult> completion)
    {
        // Unregister publishes its tracker under the registry gate; disposal callbacks
        // and cleanup-map preparation must begin only after that caller leaves the gate.
        await Task.Yield();
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
            lock (_gate)
                _unregisterOperations.Remove(assembly);
        }
    }

    private async Task CompleteTimedOutUnregisterAsync(Assembly assembly, SharpLinkDynamicModule module, Task drainTask)
    {
        await drainTask.ConfigureAwait(false);
        ReleaseModule(assembly, module);
    }

    private void ReleaseModule(Assembly assembly, SharpLinkDynamicModule module)
    {
        RpcGeneratedManifestRegistration codecRegistration;
        while (true)
        {
            // Retained cleanup storage is not management admission and remains usable after seal.
            using var storage = _runtimeContext.CreateMetadataStorage();
            using var codecSource = _runtimeContext.CaptureGeneratedCodecSnapshot();
            ClientProxySnapshot? nextSnapshot = null;
            var published = false;
            try
            {
                long generation;
                Dictionary<Type, RpcGeneratedCodecRegistration> nextFactories;
                lock (_gate)
                {
                    if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module))
                        return;
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
                    codecRegistration = module.CodecRegistration;
                    foreach (var codecType in codecRegistration.Codecs.Keys)
                    {
                        var replacement = FindReplacementCodec(codecType, module);
                        if (replacement is null) nextFactories.Remove(codecType);
                        else nextFactories[codecType] = replacement;
                    }
                }
                using var publication = _runtimeContext.PrepareGeneratedCodecPublication(nextFactories, sourceSnapshot: codecSource);
                lock (_gate)
                {
                    if (generation != _generation || !_runtimeContext.IsCurrentGeneratedCodecPublication(publication))
                        continue; // Rebuild only host maps; never reload a manifest or replay a factory.
                    _runtimeContext.CommitGeneratedCodecPublication(publication);
                    _proxyLookup.Publish(nextSnapshot);
                    _dynamicModules.Remove(assembly);
                    _generation++;
                    published = true;
                    break;
                }
            }
            finally { if (!published) nextSnapshot?.Dispose(); }
        }
        try { _runtimeContext.ReleaseGeneratedManifest(codecRegistration); }
        finally { module.MarkReleased(); }
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
}
