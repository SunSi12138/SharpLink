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

                        _runtimeContext.PublishGeneratedCodecs(candidate.Codecs);
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
        }
        return WaitForUnregisterAsync(operation, cancellationToken);
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
                        drainCompletion = new TaskCompletionSource<SharpLinkAssemblyUnregisterResult>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        drainOperation = drainCompletion.Task;
                        _dynamicModules.Add(newAssembly, newModule);
                        _detachedModuleServices.Add(oldModule, detachedServices);
                        _unregisterOperations.Add(oldAssembly, drainOperation);
                        _runtimeContext.PublishGeneratedCodecs(candidate.Codecs);
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

    private static ValueTask<SharpLinkAssemblyUnregisterResult> WaitForUnregisterAsync(
        Task<SharpLinkAssemblyUnregisterResult> operation,
        CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? new ValueTask<SharpLinkAssemblyUnregisterResult>(operation.WaitAsync(cancellationToken))
            : new ValueTask<SharpLinkAssemblyUnregisterResult>(operation);

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
                    gracefulTimeout).ConfigureAwait(false))
            {
                module.CancelRemainingCalls();
                await Task.Yield();
                if (!drainTask.IsCompleted)
                {
                    module.MarkDrainTimedOut();
                    TrackFrameworkTask(CompleteTimedOutUnregisterAsync(assembly, module, drainTask));
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
            Volatile.Write(ref _services, nextServices);
            _runtimeContext.PublishGeneratedCodecs(nextFactories);
            _dynamicModules.Remove(assembly);
            _registryGeneration++;
        }

        List<Exception>? failures = null;
        var connections = _connections.Values.Concat(_retiredConnections.Keys).Distinct().ToArray();
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
                var stub = contract.Contract.StubFactory();
                var stubCodecs = ReferenceEquals(contract.Manifest.OwnerAssembly, incoming.OwnerAssembly)
                    ? new RpcManifestCodecProvider(module.CodecRegistration, module.CodecRegistration.BaseProvider)
                    : RpcGeneratedCodecResolver.GetProvider(_runtimeContext, contract.Manifest.OwnerAssembly);
                stub.BindCodecProvider(stubCodecs);
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

            var factories = currentCodecs ?? _runtimeContext.CreateGeneratedCodecSnapshot();
            var nextFactories = factories.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            foreach (var pair in module.CodecRegistration.Codecs)
            {
                var codec = pair.Value;
                if (nextFactories.TryGetValue(pair.Key, out var existingCodec))
                {
                    if (!string.Equals(existingCodec.Factory.SchemaId, codec.Factory.SchemaId, StringComparison.Ordinal) ||
                        !string.Equals(existingCodec.Factory.WireFormatId, codec.Factory.WireFormatId, StringComparison.Ordinal))
                    {
                        error = CreateError(
                            SharpLinkAssemblyRegistrationErrorCode.CodecConflict,
                            $"Codec conflict for '{pair.Key.FullName}': existing schema/wire '{existingCodec.Factory.SchemaId}'/'{existingCodec.Factory.WireFormatId}', incoming schema/wire '{codec.Factory.SchemaId}'/'{codec.Factory.WireFormatId}'.",
                            incoming.OwnerAssembly,
                            artifact: "Codec",
                            existingFingerprint: existingCodec.Factory.SchemaId,
                            incomingFingerprint: codec.Factory.SchemaId);
                        DisposeCreatedServices(createdServices);
                        return default;
                    }
                    continue;
                }
                nextFactories.Add(pair.Key, codec);
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
            return CreateError(
                SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"Generated dependency '{dependency}' must be registered and running before '{self}'.",
                incoming.OwnerAssembly,
                artifact: "Dependency");
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

        List<Exception>? failures = null;
        for (var index = 0; index < modules.Length; index++)
        {
            var pair = modules[index];
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

    private void EnsureNoDynamicDependants(SharpLinkDynamicModule module)
    {
        var identity = module.Manifest.OwnerAssembly.FullName;
        foreach (var candidate in _dynamicModules.Values)
        {
            if (ReferenceEquals(candidate, module))
                continue;
            if (candidate.Manifest.Dependencies.Contains(identity, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Assembly '{identity}' cannot be unregistered while '{candidate.Manifest.OwnerAssembly.FullName}' depends on it.");
            }
        }
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
