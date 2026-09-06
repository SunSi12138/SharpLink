using System.Reflection;

namespace SharpLink.Client;

/// <summary>Lock-free proxy lookup facade whose immutable snapshot is owned by <see cref="ClientAssemblyRegistry"/>.</summary>
internal sealed class ClientProxyLookup
{
    private FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> _snapshot;

    internal ClientProxyLookup(FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> snapshot)
        => _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetValue(Type contractType, out SharpLinkClient.ClientProxyRegistration registration)
        => Volatile.Read(ref _snapshot).TryGetValue(contractType, out registration!);

    internal FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> Capture()
        => Volatile.Read(ref _snapshot);

    internal void Publish(FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> snapshot)
        => Volatile.Write(ref _snapshot, snapshot);
}

/// <summary>
/// Owns the client-local generated contract/proxy registry and dynamic assembly lifecycle.
/// Registry mutations are serialized here; published proxy snapshots remain immutable and lock-free for readers.
/// </summary>
internal sealed partial class ClientAssemblyRegistry
{
    private const int MaximumDynamicModules = 4_096;

    private readonly SharpLinkRuntimeContext _runtimeContext;
    private readonly IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> _staticManifests;
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
    }

    internal IReadOnlyDictionary<Assembly, SharpLinkDynamicModule> DynamicModules => _dynamicModules;

    internal FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> CaptureProxySnapshot()
        => _proxyLookup.Capture();

    internal bool TryGetProxyRegistration(
        Type contractType,
        out SharpLinkClient.ClientProxyRegistration registration)
        => _proxyLookup.TryGetValue(contractType, out registration);

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
        lock (_gate)
            _shutdownStarted = true;
    }

    internal SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
    {
        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(assembly, out var manifest);
        if (!loaded.Succeeded)
            return loaded;
        if (RejectMutation(out var state))
            return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                $"Client state '{state}' does not accept runtime assembly registration.", assembly);

        RpcGeneratedManifestRegistration? codecRegistration = null;
        SharpLinkAssemblyRegistrationError? rollbackError = null;
        Exception? rollbackException = null;
        var published = false;
        try
        {
            while (true)
            {
                long generation;
                FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> currentProxies;
                SharpLinkDynamicModule[] currentModules;
                lock (_gate)
                {
                    if (IsAssemblyRegistered(assembly))
                        return Failure(SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
                            "The same Assembly object is already registered on this client.", assembly);
                    if (_dynamicModules.Count >= MaximumDynamicModules)
                        return Failure(SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
                            $"The client runtime module limit of {MaximumDynamicModules} has been reached.", assembly);
                    generation = _generation;
                    currentProxies = _proxyLookup.Capture();
                    currentModules = [.. _dynamicModules.Values];
                }

                codecRegistration = _runtimeContext.PrepareGeneratedManifest(manifest!);
                var module = new SharpLinkDynamicModule(assembly, manifest!, codecRegistration);
                var candidate = BuildRegistrationCandidate(
                    manifest!, module, currentProxies, currentModules, currentCodecs: null, out var error);
                if (error is not null)
                {
                    rollbackError = error;
                    return SharpLinkAssemblyRegistrationResult.Failure(error);
                }

                var retry = false;
                lock (_gate)
                {
                    if (generation != _generation)
                    {
                        retry = true;
                    }
                    else
                    {
                        if (RejectMutationLocked(out state))
                        {
                            rollbackError = CreateError(
                                SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                                $"Client state '{state}' does not accept runtime assembly registration.", assembly);
                            return SharpLinkAssemblyRegistrationResult.Failure(rollbackError);
                        }
                        if (IsAssemblyRegistered(assembly))
                        {
                            rollbackError = CreateError(
                                SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
                                "The same Assembly object is already registered on this client.", assembly);
                            return SharpLinkAssemblyRegistrationResult.Failure(rollbackError);
                        }
                        var dependencyError = ValidateDependencies(
                            manifest!,
                            [.. _dynamicModules.Values]);
                        if (dependencyError is not null)
                        {
                            rollbackError = dependencyError;
                            return SharpLinkAssemblyRegistrationResult.Failure(dependencyError);
                        }
                        _runtimeContext.PublishGeneratedCodecs(candidate.Codecs, codecRegistration);
                        _runtimeContext.AdoptGeneratedManifest(codecRegistration);
                        _proxyLookup.Publish(candidate.Proxies);
                        _dynamicModules.Add(assembly, module);
                        _generation++;
                        published = true;
                        return SharpLinkAssemblyRegistrationResult.Success();
                    }
                }
                if (retry)
                {
                    var abandonedRegistration = codecRegistration;
                    codecRegistration = null;
                    abandonedRegistration.Dispose();
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            rollbackException = exception;
            rollbackError = CreateError(
                SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                $"Assembly registration failed transactionally: {exception.GetType().Name}: {exception.Message}", assembly);
            return SharpLinkAssemblyRegistrationResult.Failure(rollbackError);
        }
        finally
        {
            if (!published)
            {
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
    }

    internal ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
        Assembly oldAssembly,
        Assembly newAssembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
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

        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(newAssembly, out var manifest);
        if (!loaded.Succeeded)
            return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(loaded.Error!));

        Task<SharpLinkAssemblyUnregisterResult>? drainOperation = null;
        TaskCompletionSource<SharpLinkAssemblyUnregisterResult>? drainCompletion = null;
        SharpLinkDynamicModule? oldModule = null;
        SharpLinkDynamicModule? newModule = null;
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
                    currentModules = _dynamicModules.Values
                        .Where(module => !ReferenceEquals(module, oldModule))
                        .ToArray();
                    retainedProxies = _proxyLookup.Capture()
                        .Where(pair => !ReferenceEquals(pair.Value.Module, oldModule))
                        .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                        .ToFrozenDictionary();
                    retainedCodecs = CreateCodecSnapshotWithout(oldModule);
                }

                codecRegistration = _runtimeContext.PrepareGeneratedManifest(manifest!);
                newModule = new SharpLinkDynamicModule(newAssembly, manifest!, codecRegistration);
                var candidate = BuildRegistrationCandidate(
                    manifest!, newModule, retainedProxies, currentModules, retainedCodecs, out var error);
                if (error is not null)
                {
                    rollbackError = error;
                    return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(error));
                }

                var retry = false;
                lock (_gate)
                {
                    if (generation != _generation)
                    {
                        retry = true;
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
                        var dependencyError = ValidateDependencies(
                            manifest!,
                            _dynamicModules.Values
                                .Where(module => !ReferenceEquals(module, oldModule))
                                .ToArray());
                        if (dependencyError is not null)
                        {
                            rollbackError = dependencyError;
                            return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(dependencyError));
                        }

                        drainCompletion = new TaskCompletionSource<SharpLinkAssemblyUnregisterResult>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        drainOperation = drainCompletion.Task;
                        _runtimeContext.PublishGeneratedCodecs(candidate.Codecs, codecRegistration);
                        _dynamicModules.Add(newAssembly, newModule);
                        _unregisterOperations.Add(oldAssembly, drainOperation);
                        _runtimeContext.AdoptGeneratedManifest(codecRegistration);
                        _proxyLookup.Publish(candidate.Proxies);
                        _generation++;
                        oldModule.TryBeginDraining();
                        published = true;
                        break;
                    }
                }
                if (retry)
                {
                    var abandonedRegistration = codecRegistration;
                    codecRegistration = null;
                    newModule = null;
                    abandonedRegistration.Dispose();
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            rollbackException = exception;
            rollbackError = CreateError(
                SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                $"Assembly replacement failed transactionally: {exception.GetType().Name}: {exception.Message}",
                newAssembly);
            return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(rollbackError));
        }
        finally
        {
            if (!published)
            {
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
            _unregisterOperations.Add(assembly, operation);
            _ = CompleteUnregisterOperationAsync(assembly, module, gracefulTimeout, completion);
            if (_stateProvider() != SharpLinkConnectionState.Draining)
            {
                _trackFrameworkTask(
                    operation,
                    "DynamicAssemblyUnregister",
                    TaskObservationMode.ExternallyObserved);
            }
        }
        return WaitForUnregisterAsync(operation, cancellationToken);
    }

    internal async ValueTask<Exception[]> DrainForShutdownAsync()
    {
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

    internal bool IsDynamicAssemblyRegistered(Assembly assembly)
    {
        lock (_gate)
            return _dynamicModules.ContainsKey(assembly);
    }

    internal static FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> BuildStaticProxySnapshot(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests,
        SharpLinkRuntimeContext runtimeContext)
    {
        var registrations = new Dictionary<Type, SharpLinkClient.ClientProxyRegistration>();
        var contractIds = new Dictionary<long, ISharpLinkGeneratedAssemblyManifest>();
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
        return registrations.ToFrozenDictionary();
    }

    internal static void ValidateStaticManifestCompatibility(
        ISharpLinkGeneratedAssemblyManifest manifest)
        => SharpLinkGeneratedManifestCompatibility.ThrowIfIncompatible(manifest);

    internal static int[] GetShutdownDependencyOrder(
        string[] identities,
        string[][] dependencies)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (identities.Length != dependencies.Length)
            throw new ArgumentException("Dependency rows must match the module identity count.", nameof(dependencies));

        var remaining = new bool[identities.Length];
        Array.Fill(remaining, true);
        var order = new int[identities.Length];
        for (var outputIndex = 0; outputIndex < order.Length; outputIndex++)
        {
            var selected = -1;
            for (var candidate = 0; candidate < identities.Length; candidate++)
            {
                if (!remaining[candidate])
                    continue;

                var hasRemainingDependant = false;
                for (var dependant = 0; dependant < identities.Length; dependant++)
                {
                    if (dependant == candidate || !remaining[dependant])
                        continue;
                    if (dependencies[dependant].Any(dependency =>
                            string.Equals(dependency, identities[candidate], StringComparison.Ordinal)))
                    {
                        hasRemainingDependant = true;
                        break;
                    }
                }

                if (!hasRemainingDependant)
                {
                    selected = candidate;
                    break;
                }
            }

            if (selected < 0)
            {
                for (var candidate = identities.Length - 1; candidate >= 0; candidate--)
                {
                    if (remaining[candidate])
                    {
                        selected = candidate;
                        break;
                    }
                }
            }

            order[outputIndex] = selected;
            remaining[selected] = false;
        }

        return order;
    }

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
        lock (_gate)
        {
            if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module))
                return;
            var nextProxies = _proxyLookup.Capture()
                .Where(pair => !ReferenceEquals(pair.Value.Module, module))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                .ToFrozenDictionary();
            var factories = _runtimeContext.CreateGeneratedCodecSnapshot();
            codecRegistration = module.CodecRegistration;
            var codecTypes = codecRegistration.Codecs.Keys.ToArray();
            var nextFactories = factories.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            for (var index = 0; index < codecTypes.Length; index++)
            {
                var codecType = codecTypes[index];
                var replacement = FindReplacementCodec(codecType, module);
                if (replacement is null)
                    nextFactories.Remove(codecType);
                else
                    nextFactories[codecType] = replacement;
            }
            _runtimeContext.PublishGeneratedCodecs(nextFactories);
            _proxyLookup.Publish(nextProxies);
            _dynamicModules.Remove(assembly);
            _generation++;
        }
        try
        {
            _runtimeContext.ReleaseGeneratedManifest(codecRegistration);
        }
        finally
        {
            module.MarkReleased();
        }
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
