namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    // Public IRpcChannel casts retain an identity-neutral compatibility entry without per-call site construction.
    // Generated proxies bypass this facade and hold their registration's site directly.
    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        => _rawCallSite.InvokeUnaryAsync(method, in request, requestCodec, responseCodec, metadata, cancellationToken);

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => _rawCallSite.InvokeOneWayAsync(method, in request, requestCodec, in streams, metadata, cancellationToken);

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => _rawCallSite.InvokeClientStreamingAsync(method, in request, requestCodec, responseCodec, in streams, metadata, cancellationToken);

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        => _rawCallSite.InvokeServerStreamingAsync(method, in request, requestCodec, responseCodec, metadata, cancellationToken);

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => _rawCallSite.InvokeDuplexStreamingAsync(method, in request, requestCodec, responseCodec, in streams, metadata, cancellationToken);

    private ContractCallSite GetOrCreateCallSite(ClientProxyRegistration registration)
    {
        var existing = Volatile.Read(ref registration.CallSite);
        if (existing is not null)
            return existing;
        var created = new ContractCallSite(this, registration.Descriptor.ContractId);
        return Interlocked.CompareExchange(ref registration.CallSite, created, null) ?? created;
    }

    internal sealed partial class ContractCallSite : IRpcChannel
    {
        private readonly SharpLinkClient _owner;

        internal ContractCallSite(SharpLinkClient owner, long contractId)
        {
            _owner = owner;
            ContractId = contractId;
        }

        internal long ContractId { get; }
        public IRpcRuntimeContext RuntimeContext => _owner.RuntimeContext;

        internal ClientInterceptorGeneration CaptureInterceptorGenerationForInvocation()
            => _owner.CaptureInterceptorGenerationForInvocation();

        internal ResolvedCallControl ResolveCallControlForInvocation(
            RpcMethodDescriptor method,
            SharpLinkMetadata? metadata,
            bool includeClientDefault,
            ClientInterceptorGeneration interceptors)
            => _owner.ResolveCallControlForInvocation(method, metadata, includeClientDefault, interceptors);
    }
}
