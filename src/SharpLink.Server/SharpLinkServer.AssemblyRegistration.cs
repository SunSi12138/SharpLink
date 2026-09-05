using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private const int MaximumDynamicModules = 4_096;

    public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
    {
        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(assembly, out var manifest);
        if (!loaded.Succeeded)
            return loaded;
        if (CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
        {
            return Failure(
                SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                $"Server state '{CurrentState}' does not accept runtime assembly registration.",
                assembly);
        }

        RpcGeneratedManifestRegistration? codecRegistration = null;
        IReadOnlyDictionary<long, ServiceRegistration>? candidateServices = null;
        IReadOnlyDictionary<long, ServiceRegistration>? retainedCandidateServices = null;
        SharpLinkAssemblyRegistrationError? rollbackError = null;
        Exception? rollbackException = null;
        var published = false;
        try
        {
            while (true)
            {
                long generation;
                FrozenDictionary<long, ServiceRegistration> currentServices;
                SharpLinkDynamicModule[] currentModules;
                lock (_registryGate)
                {
                    if (IsAssemblyRegistered(assembly))
                    {
                        return Failure(
                            SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
                            "The same Assembly object is already registered on this server.",
                            assembly);
                    }
                    if (_dynamicModules.Count >= MaximumDynamicModules)
                    {
                        return Failure(
                            SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
                            $"The server runtime module limit of {MaximumDynamicModules} has been reached.",
                            assembly);
                    }
                    generation = _registryGeneration;
                    currentServices = Volatile.Read(ref _services);
                    currentModules = [.. _dynamicModules.Values];
                }

                codecRegistration = _runtimeContext.PrepareGeneratedManifest(manifest!);
                var module = new SharpLinkDynamicModule(assembly, manifest!, codecRegistration);
                var candidate = BuildRegistrationCandidate(
                    manifest!, module, currentServices, currentModules, currentCodecs: null, out var error);
                if (error is not null)
                {
                    rollbackError = error;
                    return SharpLinkAssemblyRegistrationResult.Failure(error);
                }
                candidateServices = candidate.Services;
                retainedCandidateServices = currentServices;

                var retry = false;
                lock (_registryGate)
                {
                    if (generation != _registryGeneration)
                    {
                        retry = true;
                    }
                    else
                    {
                        if (CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
                        {
                            rollbackError = CreateError(
                                SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                                $"Server state '{CurrentState}' does not accept runtime assembly registration.",
                                assembly);
                            return SharpLinkAssemblyRegistrationResult.Failure(rollbackError);
                        }
                        if (IsAssemblyRegistered(assembly))
                        {
                            rollbackError = CreateError(
                                SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
                                "The same Assembly object is already registered on this server.",
                                assembly);
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
                        Volatile.Write(ref _services, candidate.Services);
                        _dynamicModules.Add(assembly, module);
                        _registryGeneration++;
                        published = true;
                        return SharpLinkAssemblyRegistrationResult.Success();
                    }
                }
                if (retry)
                {
                    candidateServices = null;
                    retainedCandidateServices = null;
                    var abandonedRegistration = codecRegistration;
                    codecRegistration = null;
                    DisposeRegistrationCandidate(candidate.Services, currentServices, abandonedRegistration);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            rollbackException = exception;
            rollbackError = CreateError(
                SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                $"Assembly registration failed transactionally: {exception.GetType().Name}: {exception.Message}",
                assembly);
            return SharpLinkAssemblyRegistrationResult.Failure(rollbackError);
        }
        finally
        {
            if (!published)
            {
                try
                {
                    DisposeRegistrationCandidate(
                        candidateServices,
                        retainedCandidateServices,
                        codecRegistration);
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
        RpcGeneratedManifestRegistration? codecRegistration = null;
        IReadOnlyDictionary<long, ServiceRegistration>? candidateServices = null;
        IReadOnlyDictionary<long, ServiceRegistration>? retainedCandidateServices = null;
        SharpLinkAssemblyRegistrationError? rollbackError = null;
        Exception? rollbackException = null;
        var published = false;
        try
        {
            while (true)
            {
                long generation;
                SharpLinkDynamicModule[] currentModules;
                ServiceRegistration[] detachedServices;
                FrozenDictionary<long, ServiceRegistration> retainedServices;
                IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> retainedCodecs;
                lock (_registryGate)
                {
                    if (CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
                    {
                        return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(CreateError(
                            SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                            $"Server state '{CurrentState}' does not accept runtime assembly replacement.",
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
                            "The new Assembly object is already registered on this server.",
                            newAssembly)));
                    }
                    if (_dynamicModules.Count >= MaximumDynamicModules)
                    {
                        return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(CreateError(
                            SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
                            $"The server runtime module limit of {MaximumDynamicModules} has been reached; wait for a draining replacement to finish.",
                            newAssembly)));
                    }

                    var replacementDependencyError = ValidateReplacementDependants(oldModule, manifest!);
                    if (replacementDependencyError is not null)
                        return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(replacementDependencyError));

                    generation = _registryGeneration;
                    currentModules = _dynamicModules.Values
                        .Where(module => !ReferenceEquals(module, oldModule))
                        .ToArray();
                    var currentServices = Volatile.Read(ref _services);
                    detachedServices = currentServices.Values
                        .Where(service => ReferenceEquals(service.Module, oldModule))
                        .ToArray();
                    retainedServices = currentServices
                        .Where(pair => !ReferenceEquals(pair.Value.Module, oldModule))
                        .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                        .ToFrozenDictionary();
                    retainedCodecs = CreateCodecSnapshotWithout(oldModule);
                }

                codecRegistration = _runtimeContext.PrepareGeneratedManifest(manifest!);
                newModule = new SharpLinkDynamicModule(newAssembly, manifest!, codecRegistration);
                var candidate = BuildRegistrationCandidate(
                    manifest!, newModule, retainedServices, currentModules, retainedCodecs, out var error);
                if (error is not null)
                {
                    rollbackError = error;
                    return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(error));
                }
                candidateServices = candidate.Services;
                retainedCandidateServices = retainedServices;

                var retry = false;
                lock (_registryGate)
                {
                    if (generation != _registryGeneration)
                    {
                        retry = true;
                    }
                    else if (!_dynamicModules.TryGetValue(oldAssembly, out var currentOldModule) ||
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
                    else
                    {
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
                        _detachedModuleServices.Add(oldModule, detachedServices);
                        _unregisterOperations.Add(oldAssembly, drainOperation);
                        _runtimeContext.AdoptGeneratedManifest(codecRegistration);
                        Volatile.Write(ref _services, candidate.Services);
                        _registryGeneration++;
                        oldModule.TryBeginDraining();
                        published = true;
                        break;
                    }
                }
                if (retry)
                {
                    candidateServices = null;
                    retainedCandidateServices = null;
                    var abandonedRegistration = codecRegistration;
                    codecRegistration = null;
                    newModule = null;
                    DisposeRegistrationCandidate(candidate.Services, retainedServices, abandonedRegistration);
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
                    DisposeRegistrationCandidate(
                        candidateServices,
                        retainedCandidateServices,
                        codecRegistration);
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
        var drain = await new SharpLinkRetirementHandle<SharpLinkAssemblyUnregisterResult>(operation)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return SharpLinkAssemblyReplacementResult.Published(drain);
    }

    private RegistrationCandidate BuildRegistrationCandidate(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule module,
        FrozenDictionary<long, ServiceRegistration> currentServices,
        SharpLinkDynamicModule[] currentModules,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration>? currentCodecs,
        out SharpLinkAssemblyRegistrationError? error)
    {
        error = ValidateDependencies(incoming, currentModules);
        if (error is not null)
            return default;

        var contractOwners = EnumerateRegisteredManifests(currentModules)
            .SelectMany(static manifest => manifest.Contracts.Select(contract => (Contract: contract, Manifest: manifest)))
            .ToDictionary(static item => item.Contract.ContractId, static item => item);
        foreach (var contract in incoming.Contracts)
        {
            if (contractOwners.TryGetValue(contract.ContractId, out var existing))
            {
                error = Conflict(
                    SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
                    "Contract",
                    incoming,
                    contract.ContractName,
                    contract.ContractId,
                    contract.Fingerprint,
                    existing.Manifest,
                    existing.Contract.Fingerprint);
                return default;
            }
        }

        var allContracts = new Dictionary<long, (SharpLinkGeneratedContractDescriptor Contract, ISharpLinkGeneratedAssemblyManifest Manifest)>(contractOwners);
        foreach (var contract in incoming.Contracts)
            allContracts.Add(contract.ContractId, (contract, incoming));

        var factories = currentCodecs ?? _runtimeContext.CreateGeneratedCodecSnapshot();
        var nextFactories = factories.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        foreach (var pair in module.CodecRegistration.Codecs)
        {
            var codec = pair.Value;
            if (nextFactories.TryGetValue(pair.Key, out var existingCodec))
            {
                if (existingCodec.Factory.CodecHash != codec.Factory.CodecHash)
                {
                    error = CreateError(
                        SharpLinkAssemblyRegistrationErrorCode.CodecConflict,
                        $"Codec conflict for '{pair.Key.FullName}': existing CodecHash '{existingCodec.Factory.CodecHash}', incoming CodecHash '{codec.Factory.CodecHash}'.",
                        incoming.OwnerAssembly,
                        artifact: "Codec",
                        existingFingerprint: existingCodec.Factory.CodecHash.ToString(),
                        incomingFingerprint: codec.Factory.CodecHash.ToString());
                    return default;
                }
                continue;
            }
            nextFactories.Add(pair.Key, codec);
        }

        var nextServices = currentServices.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        var createdServices = new List<ServiceRegistration>();
        try
        {
            foreach (var service in incoming.Services)
            {
                if (nextServices.TryGetValue(service.ContractId, out var existingService))
                {
                    var existingManifest = FindManifest(existingService.ContractType.Assembly, currentModules);
                    error = Conflict(
                        SharpLinkAssemblyRegistrationErrorCode.ServiceConflict,
                        "Service",
                        incoming,
                        service.ContractName,
                        service.ContractId,
                        service.Fingerprint,
                        existingManifest,
                        existingService.ContractType.FullName ?? existingService.ContractType.Name);
                    DisposeCreatedServices(createdServices);
                    return default;
                }
                if (!allContracts.TryGetValue(service.ContractId, out var contract) ||
                    !ReferenceEquals(contract.Contract.ContractType, service.ContractType))
                {
                    error = CreateError(
                        SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                        $"Service '{service.ImplementationName}' requires contract '{service.ContractName}' ({service.ContractId}), but its contract manifest is not registered.",
                        incoming.OwnerAssembly,
                        artifact: "Service",
                        contractName: service.ContractName,
                        contractId: service.ContractId,
                        incomingFingerprint: service.Fingerprint);
                    DisposeCreatedServices(createdServices);
                    return default;
                }
                error = ValidateServiceDependencies(incoming, service);
                if (error is not null)
                {
                    DisposeCreatedServices(createdServices);
                    return default;
                }
                var stubCodecs = ReferenceEquals(contract.Manifest.OwnerAssembly, incoming.OwnerAssembly)
                    ? RpcGeneratedCodecResolver.GetProvider(module.CodecRegistration, contract.Contract.ContractType)
                    : RpcGeneratedCodecResolver.GetProvider(_runtimeContext, contract.Contract.ContractType);
                var stub = contract.Contract.StubFactory(stubCodecs);
                var definition = new ServiceRegistrationDefinition(
                    service.ContractType,
                    stub,
                    service.Lifetime,
                    service.Activator,
                    instance: null,
                    callerOwned: false);
                var registration = definition.Build(_serviceProvider, module);
                createdServices.Add(registration);
                nextServices.Add(service.ContractId, registration);
            }

            return new RegistrationCandidate(nextServices.ToFrozenDictionary(), nextFactories);
        }
        catch
        {
            DisposeCreatedServices(createdServices);
            throw;
        }
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

    private static void DisposeCandidateServices(
        IReadOnlyDictionary<long, ServiceRegistration> candidate,
        IReadOnlyDictionary<long, ServiceRegistration> retained)
    {
        List<Exception>? failures = null;
        foreach (var pair in candidate)
        {
            if (retained.TryGetValue(pair.Key, out var existing) && ReferenceEquals(existing, pair.Value))
                continue;
            try
            {
                SharpLinkAsyncCleanup.DisposeSynchronously(pair.Value);
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

    private static void DisposeRegistrationCandidate(
        IReadOnlyDictionary<long, ServiceRegistration>? candidateServices,
        IReadOnlyDictionary<long, ServiceRegistration>? retainedServices,
        RpcGeneratedManifestRegistration? codecRegistration)
    {
        List<Exception>? failures = null;
        if (candidateServices is not null && retainedServices is not null)
        {
            try
            {
                DisposeCandidateServices(candidateServices, retainedServices);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        try
        {
            codecRegistration?.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
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

    private static void DisposeCreatedServices(IReadOnlyList<ServiceRegistration> services)
    {
        for (var index = services.Count - 1; index >= 0; index--)
            SharpLinkAsyncCleanup.DisposeSynchronously(services[index]);
    }

    private SharpLinkAssemblyRegistrationError? ValidateReplacementDependants(
        SharpLinkDynamicModule oldModule,
        ISharpLinkGeneratedAssemblyManifest incoming)
    {
        var oldAssembly = oldModule.Manifest.OwnerAssembly;
        var oldIdentity = oldAssembly.FullName;
        var newIdentity = incoming.OwnerAssembly.FullName;
        foreach (var candidate in _dynamicModules.Values)
        {
            if (!ReferenceEquals(candidate, oldModule) &&
                ManifestDependsOn(candidate.Manifest, oldAssembly))
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

    private static IEnumerable<string> EnumerateManifestDependencies(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        foreach (var dependency in manifest.Dependencies)
            yield return dependency;
        foreach (var dependency in manifest.ContractDependencies)
            yield return dependency;
    }

    private static bool ManifestDependsOn(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly ownerAssembly)
        => SharpLinkGeneratedDependencyBinding.ManifestDependsOn(manifest, ownerAssembly);

    private SharpLinkAssemblyRegistrationError? ValidateServiceDependencies(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkGeneratedServiceDescriptor service)
    {
        if (service.Dependencies.Count == 0 ||
            _serviceProvider.GetService<IServiceProviderIsService>() is not { } availability)
        {
            return null;
        }

        for (var index = 0; index < service.Dependencies.Count; index++)
        {
            var dependency = service.Dependencies[index];
            if (availability.IsService(dependency))
                continue;
            return CreateError(
                SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"Required dependency '{dependency.FullName}' for generated RPC service " +
                $"'{service.ImplementationName}' is not registered.",
                incoming.OwnerAssembly,
                artifact: "Service",
                contractName: service.ContractName,
                contractId: service.ContractId,
                incomingFingerprint: service.Fingerprint);
        }
        return null;
    }

    private SharpLinkAssemblyRegistrationError? ValidateDependencies(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule[] currentModules)
    {
        var available = new HashSet<Assembly>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < _staticManifests.Count; index++)
            available.Add(_staticManifests[index].OwnerAssembly);
        for (var index = 0; index < currentModules.Length; index++)
        {
            var module = currentModules[index];
            if (module.State == SharpLinkDynamicModuleState.Running)
                available.Add(module.Manifest.OwnerAssembly);
        }
        var self = incoming.OwnerAssembly.FullName;
        foreach (var dependency in EnumerateManifestDependencies(incoming).Distinct(StringComparer.Ordinal))
        {
            var boundAssembly = SharpLinkGeneratedDependencyBinding.Resolve(
                incoming.OwnerAssembly,
                dependency);
            if (ReferenceEquals(boundAssembly, incoming.OwnerAssembly) ||
                boundAssembly is not null && available.Contains(boundAssembly))
            {
                continue;
            }
            return CreateError(
                SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"Generated dependency '{dependency}' must resolve through '{self}' to the exact registered and running Assembly generation before registration.",
                incoming.OwnerAssembly,
                artifact: "Dependency");
        }
        if (incoming is ISharpLinkReferencedCodecDependencyManifest referencedManifest)
        {
            foreach (var dependency in referencedManifest.ReferencedCodecDependencies)
            {
                var dependencyAssembly = dependency.TargetType.Assembly;
                if (ReferenceEquals(dependencyAssembly, incoming.OwnerAssembly) || available.Contains(dependencyAssembly))
                    continue;
                return CreateError(
                    SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                    $"Referenced generated Codec dependency '{dependency.TargetType.FullName}' must be owned by the exact registered and running Assembly generation '{dependencyAssembly.FullName}' before registration.",
                    incoming.OwnerAssembly,
                    artifact: "Dependency");
            }
        }
        return null;
    }

    private IEnumerable<ISharpLinkGeneratedAssemblyManifest> EnumerateRegisteredManifests(
        SharpLinkDynamicModule[] currentModules)
    {
        for (var index = 0; index < _staticManifests.Count; index++)
            yield return _staticManifests[index];
        for (var index = 0; index < currentModules.Length; index++)
            yield return currentModules[index].Manifest;
    }

    private ISharpLinkGeneratedAssemblyManifest FindManifest(
        Assembly assembly,
        SharpLinkDynamicModule[] currentModules)
    {
        foreach (var manifest in EnumerateRegisteredManifests(currentModules))
        {
            if (ReferenceEquals(manifest.OwnerAssembly, assembly))
                return manifest;
        }
        throw new InvalidOperationException(
            $"No registered manifest owns assembly '{assembly.FullName}'.");
    }

    private bool IsAssemblyRegistered(Assembly assembly)
    {
        if (_dynamicModules.ContainsKey(assembly))
            return true;
        for (var index = 0; index < _staticManifests.Count; index++)
        {
            if (ReferenceEquals(_staticManifests[index].OwnerAssembly, assembly))
                return true;
        }
        return false;
    }

    private static SharpLinkAssemblyRegistrationResult Failure(
        SharpLinkAssemblyRegistrationErrorCode code,
        string message,
        Assembly assembly)
        => SharpLinkAssemblyRegistrationResult.Failure(CreateError(code, message, assembly));

    private static SharpLinkAssemblyRegistrationError CreateError(
        SharpLinkAssemblyRegistrationErrorCode code,
        string message,
        Assembly assembly,
        string? artifact = null,
        string? contractName = null,
        long? contractId = null,
        string? existingFingerprint = null,
        string? incomingFingerprint = null)
        => new(
            code,
            message,
            SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(assembly),
            IncomingLoadContext: SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(assembly),
            Artifact: artifact,
            ContractName: contractName,
            ContractId: contractId,
            ExistingFingerprint: existingFingerprint,
            IncomingFingerprint: incomingFingerprint);

    private static SharpLinkAssemblyRegistrationError Conflict(
        SharpLinkAssemblyRegistrationErrorCode code,
        string artifact,
        ISharpLinkGeneratedAssemblyManifest incoming,
        string contractName,
        long contractId,
        string incomingFingerprint,
        ISharpLinkGeneratedAssemblyManifest existing,
        string existingFingerprint)
        => new(
            code,
            $"{artifact} conflict for '{contractName}' ({contractId}). Incoming Assembly='{incoming.OwnerAssembly.FullName}', " +
            $"ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(incoming.OwnerAssembly)}', Fingerprint='{incomingFingerprint}'; " +
            $"Existing Assembly='{existing.OwnerAssembly.FullName}', " +
            $"ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(existing.OwnerAssembly)}', Fingerprint='{existingFingerprint}'.",
            SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(incoming.OwnerAssembly),
            SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(existing.OwnerAssembly),
            SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(incoming.OwnerAssembly),
            SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(existing.OwnerAssembly),
            artifact,
            contractName,
            contractId,
            ExistingFingerprint: existingFingerprint,
            IncomingFingerprint: incomingFingerprint);

    private readonly record struct RegistrationCandidate(
        FrozenDictionary<long, ServiceRegistration> Services,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Codecs);
}
