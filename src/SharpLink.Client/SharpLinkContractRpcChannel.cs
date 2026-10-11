namespace SharpLink.Client;

internal sealed class SharpLinkContractRpcChannel(
    SharpLinkClient inner,
    ContractRequirement requirement) : IRpcChannel
{
    public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;

    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
        => inner.InvokeUnaryAsync(requirement, method, request, requestCodec, responseCodec,
            callMetadata, cancellationToken);

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, in TStreams streams,
        SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeOneWayAsync(requirement, method, request, requestCodec, streams,
            callMetadata, cancellationToken);

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeClientStreamingAsync(requirement, method, request, requestCodec, responseCodec, streams,
            callMetadata, cancellationToken);

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        => inner.InvokeServerStreamingAsync(requirement, method, request, requestCodec, responseCodec,
            callMetadata, cancellationToken);

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeDuplexStreamingAsync(requirement, method, request, requestCodec, responseCodec, streams,
            callMetadata, cancellationToken);

    public Task SendClientStreamAsync<T>(long requestId, ushort streamId,
        IAsyncEnumerable<T> stream, IRpcCodec<T> codec, CancellationToken cancellationToken = default)
        => inner.SendClientStreamAsync(requestId, streamId, stream, codec, cancellationToken);
}
