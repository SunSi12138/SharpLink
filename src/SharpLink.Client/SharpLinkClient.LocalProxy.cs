namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public T CreateProxy<T>(RpcProxyOptions? options = null) where T : IService
    {
        var registration = GetLocalProxyRegistration<T>();
        var binding = registration.Binding.WithOptions(options);
        if (ReferenceEquals(binding, registration.Binding))
        {
            var existing = Volatile.Read(ref registration.Proxy);
            if (existing is not null)
                return (T)existing;
            var created = CreateProxyInstance(registration, binding);
            return (T)(Interlocked.CompareExchange(ref registration.Proxy, created, null) ?? created);
        }
        return (T)CreateProxyInstance(registration, binding);
    }

    private ClientProxyRegistration GetLocalProxyRegistration<T>() where T : IService
    {
        if (Volatile.Read(ref _stopStarted) != 0 ||
            State is SharpLinkConnectionState.Draining or SharpLinkConnectionState.Stopped or SharpLinkConnectionState.Faulted)
        {
            throw CreateConnectionClosedException("Client is not accepting new proxies.");
        }
        if (!_proxies.TryGetValue(typeof(T), out var registration))
            throw new InvalidOperationException($"Proxy for service interface {typeof(T).FullName} is not registered.");
        if (registration.Binding.RpcAssemblyHash.IsEmpty)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "The local contract has no RPC assembly hash.");
        if (registration.Module is { State: not SharpLinkDynamicModuleState.Running })
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                SharpLinkErrorDetails.Unavailable.RegistrationRetired, "RPC module is draining.");
        return registration;
    }

    private IRpcChannel CreateBoundChannel(ClientProxyRegistration registration, ClientContractBinding binding)
        => registration.Module is { } module
            ? new SharpLinkModuleRpcChannel(this, module, binding)
            : new SharpLinkContractRpcChannel(this, binding);

    private object CreateProxyInstance(ClientProxyRegistration registration, ClientContractBinding binding,
        SharpLinkMetadata? metadata = null)
    {
        SharpLinkDynamicModuleLease lease = default;
        if (registration.Module is { } module && !module.TryAcquire(false, out lease))
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                SharpLinkErrorDetails.Unavailable.RegistrationRetired, "RPC module is draining.");
        try
        {
            var channel = CreateBoundChannel(registration, binding);
            if (metadata is { Count: > 0 })
                channel = new SharpLinkMetadataRpcChannel(channel, metadata);
            return registration.Descriptor.ProxyFactory(channel, registration.Codecs);
        }
        finally { lease.Dispose(); }
    }

    private ClientContractBinding ResolveLocalBinding(RpcMethodDescriptor method)
    {
        if (_proxies.TryGetBinding(method.ContractId, out var binding))
            return binding;
        throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "The RPC contract is not registered locally.");
    }

    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        => InvokeUnaryBoundAsync(method, request, requestCodec, responseCodec, metadata, cancellationToken, ResolveLocalBinding(method));

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, in TStreams streams,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => InvokeOneWayBoundAsync(method, request, requestCodec, streams, metadata, cancellationToken, ResolveLocalBinding(method));

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => InvokeClientStreamingBoundAsync(method, request, requestCodec, responseCodec, streams, metadata, cancellationToken, ResolveLocalBinding(method));

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        => InvokeServerStreamingBoundAsync(method, request, requestCodec, responseCodec, metadata, cancellationToken, ResolveLocalBinding(method));

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => InvokeDuplexStreamingBoundAsync(method, request, requestCodec, responseCodec, streams, metadata, cancellationToken, ResolveLocalBinding(method));
}
