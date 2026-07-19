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

        try
        {
            var module = new SharpLinkDynamicModule(assembly, manifest!);
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

                var candidate = BuildRegistrationCandidate(
                    manifest!, module, currentProxies, currentModules, out var error);
                if (error is not null)
                    return SharpLinkAssemblyRegistrationResult.Failure(error);

                lock (_registryGate)
                {
                    if (generation != _registryGeneration)
                        continue;
                    if (State is SharpLinkConnectionState.Draining or
                        SharpLinkConnectionState.Stopped or SharpLinkConnectionState.Faulted)
                    {
                        return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                            $"Client state '{State}' does not accept runtime assembly registration.", assembly);
                    }
                    if (IsAssemblyRegistered(assembly))
                        return Failure(SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
                            "The same Assembly object is already registered on this client.", assembly);
                    _runtimeContext.PublishGeneratedCodecs(candidate.Codecs);
                    Volatile.Write(ref _proxies, candidate.Proxies);
                    _dynamicModules.Add(assembly, module);
                    _registryGeneration++;
                    return SharpLinkAssemblyRegistrationResult.Success();
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                $"Assembly registration failed transactionally: {exception.GetType().Name}: {exception.Message}", assembly);
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
                return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
            EnsureNoDynamicDependants(module);
            var completion = new TaskCompletionSource<SharpLinkAssemblyUnregisterResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            operation = completion.Task;
            _unregisterOperations.Add(assembly, operation);
            _ = CompleteUnregisterOperationAsync(assembly, module, gracefulTimeout, completion);
        }
        return WaitForUnregisterAsync(operation, cancellationToken);
    }

    private static FrozenDictionary<Type, ClientProxyRegistration> BuildStaticProxySnapshot(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
    {
        var registrations = new Dictionary<Type, ClientProxyRegistration>();
        var contractIds = new Dictionary<long, ISharpLinkGeneratedAssemblyManifest>();
        for (var manifestIndex = 0; manifestIndex < manifests.Count; manifestIndex++)
        {
            var manifest = manifests[manifestIndex];
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

    private RegistrationCandidate BuildRegistrationCandidate(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule module,
        FrozenDictionary<Type, ClientProxyRegistration> currentProxies,
        SharpLinkDynamicModule[] currentModules,
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

        var nextFactories = _runtimeContext.CreateGeneratedCodecSnapshot()
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        foreach (var codec in incoming.Codecs)
        {
            if (nextFactories.TryGetValue(codec.TargetType, out var existingCodec))
            {
                if (!string.Equals(existingCodec.SchemaId, codec.SchemaId, StringComparison.Ordinal))
                {
                    error = CreateError(SharpLinkAssemblyRegistrationErrorCode.CodecConflict,
                        $"Codec conflict for '{codec.TargetType.FullName}': existing schema '{existingCodec.SchemaId}', incoming schema '{codec.SchemaId}'.",
                        incoming.OwnerAssembly, "Codec", existingCodec.SchemaId, codec.SchemaId);
                    return default;
                }
                continue;
            }
            nextFactories.Add(codec.TargetType, codec);
        }
        return new RegistrationCandidate(nextProxies.ToFrozenDictionary(), nextFactories);
    }

    private SharpLinkAssemblyRegistrationError? ValidateDependencies(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule[] currentModules)
    {
        var available = new HashSet<string>(EnumerateRegisteredManifests(currentModules)
            .Select(static manifest => manifest.OwnerAssembly.FullName ?? string.Empty), StringComparer.Ordinal);
        var self = incoming.OwnerAssembly.FullName;
        foreach (var dependency in incoming.Dependencies)
        {
            if (string.Equals(dependency, self, StringComparison.Ordinal) || available.Contains(dependency))
                continue;
            return CreateError(SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"Generated dependency '{dependency}' must be registered before '{self}'.",
                incoming.OwnerAssembly, "Dependency");
        }
        return null;
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
            if (!ReferenceEquals(await Task.WhenAny(drainTask, Task.Delay(gracefulTimeout)).ConfigureAwait(false), drainTask))
            {
                module.CancelRemainingCalls();
                await Task.Yield();
                if (!drainTask.IsCompleted)
                {
                    module.MarkDrainTimedOut();
                    TrackBackgroundTask(CompleteTimedOutUnregisterAsync(assembly, module, drainTask));
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
            lock (_registryGate)
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
        Type[] codecTypes;
        lock (_registryGate)
        {
            if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module))
                return;
            var nextProxies = Volatile.Read(ref _proxies)
                .Where(pair => !ReferenceEquals(pair.Value.Module, module))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                .ToFrozenDictionary();
            var factories = _runtimeContext.CreateGeneratedCodecSnapshot();
            codecTypes = module.Manifest.Codecs.Select(static codec => codec.TargetType).ToArray();
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
            Volatile.Write(ref _proxies, nextProxies);
            _runtimeContext.PublishGeneratedCodecs(nextFactories);
            _dynamicModules.Remove(assembly);
            _registryGeneration++;
        }
        _runtimeContext.RemoveResolvedGeneratedCodecs(codecTypes);
        module.MarkReleased();
    }

    private IRpcGeneratedCodecFactory? FindReplacementCodec(
        Type targetType,
        SharpLinkDynamicModule removedModule)
    {
        for (var index = 0; index < _staticManifests.Count; index++)
        {
            var replacement = _staticManifests[index].Codecs.FirstOrDefault(codec => codec.TargetType == targetType);
            if (replacement is not null)
                return replacement;
        }
        foreach (var candidate in _dynamicModules.Values)
        {
            if (ReferenceEquals(candidate, removedModule))
                continue;
            var replacement = candidate.Manifest.Codecs.FirstOrDefault(codec => codec.TargetType == targetType);
            if (replacement is not null)
                return replacement;
        }
        return null;
    }

    private static ValueTask<SharpLinkAssemblyUnregisterResult> WaitForUnregisterAsync(
        Task<SharpLinkAssemblyUnregisterResult> operation,
        CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? new ValueTask<SharpLinkAssemblyUnregisterResult>(operation.WaitAsync(cancellationToken))
            : new ValueTask<SharpLinkAssemblyUnregisterResult>(operation);

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

    private void EnsureNoDynamicDependants(SharpLinkDynamicModule module)
    {
        var identity = module.Manifest.OwnerAssembly.FullName;
        foreach (var candidate in _dynamicModules.Values)
        {
            if (!ReferenceEquals(candidate, module) &&
                candidate.Manifest.Dependencies.Contains(identity, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"Assembly '{identity}' cannot be unregistered while '{candidate.Manifest.OwnerAssembly.FullName}' depends on it.");
        }
    }

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

    private sealed record ClientProxyRegistration(
        SharpLinkGeneratedContractDescriptor Descriptor,
        SharpLinkDynamicModule? Module);

    private readonly record struct RegistrationCandidate(
        FrozenDictionary<Type, ClientProxyRegistration> Proxies,
        IReadOnlyDictionary<Type, IRpcGeneratedCodecFactory> Codecs);
}

internal sealed class SharpLinkModuleRpcChannel(IRpcChannel inner, SharpLinkDynamicModule module) : IRpcChannel
{
    public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;

    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method, in TRequest request,
        IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!module.TryAcquire(false, out var lease))
            return ValueTask.FromException<TResponse>(Draining());
        var combined = Combine(cancellationToken, module.ForcedCancellation);
        try
        {
            var call = inner.InvokeUnaryAsync(method, request, requestCodec, responseCodec, options, combined.Token);
            if (call.IsCompletedSuccessfully)
            {
                lease.Dispose();
                combined.Dispose();
                return call;
            }
            return AwaitAsync(call, lease, combined);
        }
        catch { lease.Dispose(); combined.Dispose(); throw; }
    }

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method, in TRequest request,
        IRpcCodec<TRequest> requestCodec, in TStreams streams, SharpLinkCallOptions options,
        CancellationToken cancellationToken = default) where TStreams : struct, IRpcClientStreamWriter
    {
        if (!module.TryAcquire(method.HasClientStreams, out var lease))
            return ValueTask.FromException(Draining());
        var combined = Combine(cancellationToken, module.ForcedCancellation);
        try
        {
            var call = inner.InvokeOneWayAsync(method, request, requestCodec, streams, options, combined.Token);
            if (call.IsCompletedSuccessfully)
            {
                lease.Dispose();
                combined.Dispose();
                return call;
            }
            return AwaitAsync(call, lease, combined);
        }
        catch { lease.Dispose(); combined.Dispose(); throw; }
    }

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkCallOptions options, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        if (!module.TryAcquire(true, out var lease))
            return ValueTask.FromException<TResponse>(Draining());
        var combined = Combine(cancellationToken, module.ForcedCancellation);
        try
        {
            var call = inner.InvokeClientStreamingAsync(
                method, request, requestCodec, responseCodec, streams, options, combined.Token);
            if (call.IsCompletedSuccessfully)
            {
                lease.Dispose();
                combined.Dispose();
                return call;
            }
            return AwaitAsync(call, lease, combined);
        }
        catch { lease.Dispose(); combined.Dispose(); throw; }
    }

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options, CancellationToken cancellationToken = default)
    {
        var requestValue = request;
        return InvokeServerStreamingDeferred(
            method, requestValue, requestCodec, responseCodec, options, cancellationToken);
    }

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkCallOptions options, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var requestValue = request;
        var streamsValue = streams;
        return InvokeDuplexStreamingDeferred(
            method, requestValue, requestCodec, responseCodec, streamsValue, options, cancellationToken);
    }

    public Task SendClientStreamAsync<T>(long requestId, ushort streamId, IAsyncEnumerable<T> stream,
        CancellationToken cancellationToken = default)
        => inner.SendClientStreamAsync(requestId, streamId, stream, cancellationToken);

    private static async ValueTask<T> AwaitAsync<T>(ValueTask<T> call, SharpLinkDynamicModuleLease lease,
        CombinedCancellation combined)
    {
        try { return await call.ConfigureAwait(false); }
        finally { lease.Dispose(); combined.Dispose(); }
    }

    private static async ValueTask AwaitAsync(ValueTask call, SharpLinkDynamicModuleLease lease,
        CombinedCancellation combined)
    {
        try { await call.ConfigureAwait(false); }
        finally { lease.Dispose(); combined.Dispose(); }
    }

    private async IAsyncEnumerable<TResponse> InvokeServerStreamingDeferred<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options,
        CancellationToken callCancellation,
        [EnumeratorCancellation] CancellationToken enumerationCancellation = default)
    {
        if (!module.TryAcquire(true, out var lease))
            throw Draining();
        var combined = Combine(callCancellation, module.ForcedCancellation);
        try
        {
            var stream = inner.InvokeServerStreamingAsync(
                method, request, requestCodec, responseCodec, options, combined.Token);
            await foreach (var item in stream.WithCancellation(enumerationCancellation).ConfigureAwait(false))
                yield return item;
        }
        finally { lease.Dispose(); combined.Dispose(); }
    }

    private async IAsyncEnumerable<TResponse> InvokeDuplexStreamingDeferred<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken callCancellation,
        [EnumeratorCancellation] CancellationToken enumerationCancellation = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        if (!module.TryAcquire(true, out var lease))
            throw Draining();
        var combined = Combine(callCancellation, module.ForcedCancellation);
        try
        {
            var stream = inner.InvokeDuplexStreamingAsync(
                method, request, requestCodec, responseCodec, streams, options, combined.Token);
            await foreach (var item in stream.WithCancellation(enumerationCancellation).ConfigureAwait(false))
                yield return item;
        }
        finally { lease.Dispose(); combined.Dispose(); }
    }

    private static SharpLinkException Draining() => new(SharpLinkErrorCode.Unavailable, "RPC module is draining");

    private static CombinedCancellation Combine(CancellationToken caller, CancellationToken moduleToken)
    {
        if (!caller.CanBeCanceled)
            return new CombinedCancellation(moduleToken, null);
        var source = CancellationTokenSource.CreateLinkedTokenSource(caller, moduleToken);
        return new CombinedCancellation(source.Token, source);
    }

    private readonly struct CombinedCancellation(CancellationToken token, CancellationTokenSource? source) : IDisposable
    {
        internal CancellationToken Token { get; } = token;
        public void Dispose() => source?.Dispose();
    }
}
