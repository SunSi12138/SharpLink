using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private const int MaximumDynamicModules = 4_096;

    public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
    {
        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(assembly, out var manifest);
        if (!loaded.Succeeded)
            return loaded;
        if (State is SharpLinkConnectionState.Draining or SharpLinkConnectionState.Stopped or SharpLinkConnectionState.Faulted)
            return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                $"Client state '{State}' does not accept runtime assembly registration.", assembly);

        RpcContractCodecSet? codecRegistration = null;
        SharpLinkAssemblyRegistrationError? rollbackError = null;
        Exception? rollbackException = null;
        var published = false;
        try
        {
            while (true)
            {
                long generation;
                FrozenDictionary<Type, ClientProxyRegistration> currentProxies;
                SharpLinkDynamicModule[] currentModules;
                lock (_registryGate)
                {
                    if (IsAssemblyRegistered(assembly))
                        return Failure(SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
                            "The same Assembly object is already registered on this client.", assembly);
                    if (_dynamicModules.Count >= MaximumDynamicModules)
                        return Failure(SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
                            $"The client runtime module limit of {MaximumDynamicModules} has been reached.", assembly);
                    generation = _registryGeneration;
                    currentProxies = Volatile.Read(ref _proxies);
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
                lock (_registryGate)
                {
                    if (generation != _registryGeneration)
                    {
                        retry = true;
                    }
                    else
                    {
                        if (State is SharpLinkConnectionState.Draining or
                            SharpLinkConnectionState.Stopped or SharpLinkConnectionState.Faulted)
                        {
                            rollbackError = CreateError(
                                SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                                $"Client state '{State}' does not accept runtime assembly registration.", assembly);
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
                        _runtimeContext.PublishGeneratedCodecs(candidate.Codecs);
                        _runtimeContext.AdoptGeneratedManifest(codecRegistration);
                        Volatile.Write(ref _proxies, candidate.Proxies);
                        _dynamicModules.Add(assembly, module);
                        _registryGeneration++;
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

    public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
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
        RpcContractCodecSet? codecRegistration = null;
        SharpLinkAssemblyRegistrationError? rollbackError = null;
        Exception? rollbackException = null;
        var published = false;
        try
        {
            while (true)
            {
                long generation;
                FrozenDictionary<Type, ClientProxyRegistration> retainedProxies;
                SharpLinkDynamicModule[] currentModules;
                IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> retainedCodecs;
                lock (_registryGate)
                {
                    if (State is SharpLinkConnectionState.Draining or
                        SharpLinkConnectionState.Stopped or SharpLinkConnectionState.Faulted)
                    {
                        return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(CreateError(
                            SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                            $"Client state '{State}' does not accept runtime assembly replacement.",
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

                    generation = _registryGeneration;
                    currentModules = _dynamicModules.Values
                        .Where(module => !ReferenceEquals(module, oldModule))
                        .ToArray();
                    retainedProxies = Volatile.Read(ref _proxies)
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
                lock (_registryGate)
                {
                    if (generation != _registryGeneration)
                    {
                        retry = true;
                    }
                    else
                    {
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
                        _dynamicModules.Add(newAssembly, newModule);
                        _unregisterOperations.Add(oldAssembly, drainOperation);
                        _runtimeContext.PublishGeneratedCodecs(candidate.Codecs);
                        _runtimeContext.AdoptGeneratedManifest(codecRegistration);
                        Volatile.Write(ref _proxies, candidate.Proxies);
                        _registryGeneration++;
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

        TrackFrameworkTask(
            drainOperation!,
            "DynamicAssemblyReplacementDrain",
            TaskObservationMode.ExternallyObserved);
        _ = CompleteUnregisterOperationAsync(oldAssembly, oldModule!, gracefulTimeout, drainCompletion!);
        return WaitForReplacementAsync(drainOperation!, cancellationToken);
    }

    private static async ValueTask<SharpLinkAssemblyReplacementResult> WaitForReplacementAsync(
        Task<SharpLinkAssemblyUnregisterResult> operation,
        CancellationToken cancellationToken)
    {
        var drain = cancellationToken.CanBeCanceled
            ? await operation.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await operation.ConfigureAwait(false);
        return SharpLinkAssemblyReplacementResult.Published(drain);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowAfterAssemblyRollback(
        SharpLinkAssemblyRegistrationError? rollbackError,
        Exception? rollbackException,
        Exception cleanupException)
    {
        if (rollbackException is not null)
            throw new AggregateException(rollbackException, cleanupException);
        if (rollbackError is not null)
        {
            throw new AggregateException(
                new InvalidOperationException($"{rollbackError.Code}: {rollbackError.Message}"),
                cleanupException);
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
        throw new System.Diagnostics.UnreachableException();
    }

    internal static FrozenDictionary<Type, ClientProxyRegistration> BuildStaticProxySnapshot(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
    {
        var registrations = new Dictionary<Type, ClientProxyRegistration>();
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
                registrations.Add(contract.ContractType, new ClientProxyRegistration(contract, null));
            }
        }
        return registrations.ToFrozenDictionary();
    }

    internal static void ValidateStaticManifestCompatibility(
        ISharpLinkGeneratedAssemblyManifest manifest)
        => SharpLinkGeneratedManifestCompatibility.ThrowIfIncompatible(manifest);

    private RegistrationCandidate BuildRegistrationCandidate(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule module,
        FrozenDictionary<Type, ClientProxyRegistration> currentProxies,
        SharpLinkDynamicModule[] currentModules,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration>? currentCodecs,
        out SharpLinkAssemblyRegistrationError? error)
    {
        error = ValidateDependencies(incoming, currentModules);
        if (error is not null)
            return default;

        var byId = currentProxies.Values.ToDictionary(
            static registration => registration.Descriptor.ContractId,
            static registration => registration);
        var nextProxies = currentProxies.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        foreach (var contract in incoming.Contracts)
        {
            if (byId.TryGetValue(contract.ContractId, out var existing))
            {
                error = Conflict(incoming, contract,
                    FindManifest(existing.Descriptor.ContractType.Assembly, currentModules), existing.Descriptor);
                return default;
            }
            var registration = new ClientProxyRegistration(contract, module);
            nextProxies.Add(contract.ContractType, registration);
            byId.Add(contract.ContractId, registration);
        }

        var nextFactories = (currentCodecs ?? _runtimeContext.CreateGeneratedCodecSnapshot())
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        foreach (var pair in module.CodecRegistration.Codecs)
        {
            var codec = pair.Value;
            if (nextFactories.TryGetValue(pair.Key, out var existingCodec))
            {
                if (!string.Equals(existingCodec.Factory.SchemaId, codec.Factory.SchemaId, StringComparison.Ordinal) ||
                    !string.Equals(existingCodec.Factory.WireFormatId, codec.Factory.WireFormatId, StringComparison.Ordinal))
                {
                    error = CreateError(SharpLinkAssemblyRegistrationErrorCode.CodecConflict,
                        $"Codec conflict for '{pair.Key.FullName}': existing schema/wire '{existingCodec.Factory.SchemaId}'/'{existingCodec.Factory.WireFormatId}', incoming schema/wire '{codec.Factory.SchemaId}'/'{codec.Factory.WireFormatId}'.",
                        incoming.OwnerAssembly, "Codec", existingCodec.Factory.SchemaId, codec.Factory.SchemaId);
                    return default;
                }
                continue;
            }
            nextFactories.Add(pair.Key, codec);
        }
        return new RegistrationCandidate(nextProxies.ToFrozenDictionary(), nextFactories);
    }

    private IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> CreateCodecSnapshotWithout(
        SharpLinkDynamicModule removedModule)
    {
        var nextFactories = _runtimeContext.CreateGeneratedCodecSnapshot()
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        foreach (var codec in removedModule.CodecRegistration.Codecs)
        {
            var replacement = FindReplacementCodec(codec.Key, removedModule);
            if (replacement is null)
                nextFactories.Remove(codec.Key);
            else
                nextFactories[codec.Key] = replacement;
        }
        return nextFactories;
    }

    private SharpLinkAssemblyRegistrationError? ValidateReplacementDependants(
        SharpLinkDynamicModule oldModule,
        ISharpLinkGeneratedAssemblyManifest incoming)
    {
        var oldIdentity = oldModule.Manifest.OwnerAssembly.FullName;
        var newIdentity = incoming.OwnerAssembly.FullName;
        if (string.Equals(oldIdentity, newIdentity, StringComparison.Ordinal))
            return null;
        foreach (var candidate in _dynamicModules.Values)
        {
            if (!ReferenceEquals(candidate, oldModule) &&
                candidate.Manifest.Dependencies.Contains(oldIdentity, StringComparer.Ordinal))
            {
                return CreateError(
                    SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                    $"Assembly '{candidate.Manifest.OwnerAssembly.FullName}' depends on '{oldIdentity}', " +
                    $"so it cannot remain registered after replacement by '{newIdentity}'.",
                    incoming.OwnerAssembly,
                    artifact: "Dependency");
            }
        }
        return null;
    }

    private SharpLinkAssemblyRegistrationError? ValidateDependencies(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule[] currentModules)
    {
        var available = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < _staticManifests.Count; index++)
            available.Add(_staticManifests[index].OwnerAssembly.FullName ?? string.Empty);
        for (var index = 0; index < currentModules.Length; index++)
        {
            var module = currentModules[index];
            if (module.State == SharpLinkDynamicModuleState.Running)
                available.Add(module.Manifest.OwnerAssembly.FullName ?? string.Empty);
        }
        var self = incoming.OwnerAssembly.FullName;
        foreach (var dependency in incoming.Dependencies)
        {
            if (string.Equals(dependency, self, StringComparison.Ordinal) || available.Contains(dependency))
                continue;
            return CreateError(SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"Generated dependency '{dependency}' must be registered and running before '{self}'.",
                incoming.OwnerAssembly, "Dependency");
        }
        return null;
    }

    internal bool IsDynamicAssemblyRegistered(Assembly assembly)
    {
        lock (_registryGate)
            return _dynamicModules.ContainsKey(assembly);
    }

    bool IDynamicAssemblyRegistrationInspector.IsDynamicAssemblyRegistered(Assembly assembly)
        => IsDynamicAssemblyRegistered(assembly);

    private IEnumerable<ISharpLinkGeneratedAssemblyManifest> EnumerateRegisteredManifests(SharpLinkDynamicModule[] modules)
    {
        for (var index = 0; index < _staticManifests.Count; index++)
            yield return _staticManifests[index];
        for (var index = 0; index < modules.Length; index++)
            yield return modules[index].Manifest;
    }

    private ISharpLinkGeneratedAssemblyManifest FindManifest(Assembly assembly, SharpLinkDynamicModule[] modules)
        => EnumerateRegisteredManifests(modules).First(manifest => ReferenceEquals(manifest.OwnerAssembly, assembly));

    private bool IsAssemblyRegistered(Assembly assembly)
        => _dynamicModules.ContainsKey(assembly) ||
           _staticManifests.Any(manifest => ReferenceEquals(manifest.OwnerAssembly, assembly));

    private static SharpLinkAssemblyRegistrationResult Failure(
        SharpLinkAssemblyRegistrationErrorCode code, string message, Assembly assembly)
        => SharpLinkAssemblyRegistrationResult.Failure(CreateError(code, message, assembly));

    private static SharpLinkAssemblyRegistrationError CreateError(
        SharpLinkAssemblyRegistrationErrorCode code,
        string message,
        Assembly assembly,
        string? artifact = null,
        string? existingFingerprint = null,
        string? incomingFingerprint = null)
        => new(code, message,
            SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(assembly),
            IncomingLoadContext: SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(assembly),
            Artifact: artifact,
            ExistingFingerprint: existingFingerprint,
            IncomingFingerprint: incomingFingerprint);

    private static SharpLinkAssemblyRegistrationError Conflict(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkGeneratedContractDescriptor incomingContract,
        ISharpLinkGeneratedAssemblyManifest existing,
        SharpLinkGeneratedContractDescriptor existingContract)
        => new(SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
            $"Contract conflict for '{incomingContract.ContractName}' ({incomingContract.ContractId}). " +
            $"Incoming Assembly='{incoming.OwnerAssembly.FullName}', ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(incoming.OwnerAssembly)}', Fingerprint='{incomingContract.Fingerprint}'; " +
            $"Existing Assembly='{existing.OwnerAssembly.FullName}', ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(existing.OwnerAssembly)}', Fingerprint='{existingContract.Fingerprint}'.",
            SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(incoming.OwnerAssembly),
            SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(existing.OwnerAssembly),
            SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(incoming.OwnerAssembly),
            SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(existing.OwnerAssembly),
            "Contract", incomingContract.ContractName, incomingContract.ContractId,
            ExistingFingerprint: existingContract.Fingerprint,
            IncomingFingerprint: incomingContract.Fingerprint);

    internal sealed record ClientProxyRegistration(
        SharpLinkGeneratedContractDescriptor Descriptor,
        SharpLinkDynamicModule? Module);

    private readonly record struct RegistrationCandidate(
        FrozenDictionary<Type, ClientProxyRegistration> Proxies,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Codecs);
}
