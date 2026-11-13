#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Client;

internal sealed partial class StaticBindingCallSite : IRpcContractChannel
{
    public IRpcRuntimeContext RuntimeContext => Owner.RuntimeContext;
    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => Owner.PrepareStaticBindingAsync(this, timeout, cancellationToken);

    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        => Owner.InvokeStaticUnaryAsync(this, method, request, requestCodec, responseCodec, metadata, cancellationToken);

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, in TStreams streams,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => Owner.InvokeStaticOneWayAsync(this, method, request, requestCodec, streams, metadata, cancellationToken);

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter => ValueTask.FromException<TResponse>(Unsupported());

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default) => throw Unsupported();

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter => throw Unsupported();

    private static SharpLinkException Unsupported() => StaticBindingWire.Error(StaticBindingFailure.UnsupportedShape);
}
#endif
