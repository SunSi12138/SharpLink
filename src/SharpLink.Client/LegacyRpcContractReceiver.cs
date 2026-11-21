namespace SharpLink.Client;

// Temporary adapter for old Get/GetWithMetadata callers in the private study.
// Prepared generated and handwritten callers never use this adapter.
internal sealed class LegacyRpcContractReceiver(
    IRpcChannel inner,
    SharpLinkMetadata? metadata = null) : IRpcContractChannel
{
    public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;

    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => ValueTask.FromException(new NotSupportedException("This legacy receiver cannot prepare a contract."));

    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
        => inner.InvokeUnaryAsync(method, request, requestCodec, responseCodec,
            callMetadata ?? metadata, cancellationToken);

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, in TStreams streams,
        SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeOneWayAsync(method, request, requestCodec, streams,
            callMetadata ?? metadata, cancellationToken);

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeClientStreamingAsync(method, request, requestCodec, responseCodec, streams,
            callMetadata ?? metadata, cancellationToken);

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        => inner.InvokeServerStreamingAsync(method, request, requestCodec, responseCodec,
            callMetadata ?? metadata, cancellationToken);

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeDuplexStreamingAsync(method, request, requestCodec, responseCodec, streams,
            callMetadata ?? metadata, cancellationToken);

}
