namespace SharpLink.Abstractions;

/// <summary>Identifies one generated RPC invocation shape.</summary>
public enum RpcMethodKind : byte
{
    /// <summary>One request followed by one response.</summary>
    Unary,

    /// <summary>A locally accepted request that has no response.</summary>
    OneWay,

    /// <summary>One or more client streams followed by one response.</summary>
    ClientStreaming,

    /// <summary>One request followed by a server stream.</summary>
    ServerStreaming,

    /// <summary>One or more client streams paired with a server stream.</summary>
    DuplexStreaming
}

/// <summary>Immutable metadata emitted once for an RPC contract method.</summary>
/// <param name="ContractId">Stable generated contract identifier.</param>
/// <param name="MethodId">Stable generated method identifier.</param>
/// <param name="Kind">Invocation shape.</param>
/// <param name="HasResponsePayload">Whether a successful response contains a business payload.</param>
/// <param name="HasClientStreams">Whether the request owns one or more client streams.</param>
/// <param name="HasMethodTimeout">Whether the contract method declares <c>[Timeout]</c>.</param>
/// <param name="MethodTimeout">Explicit method timeout, or <see langword="null"/> to use the client default.</param>
public readonly record struct RpcMethodDescriptor(
    long ContractId,
    long MethodId,
    RpcMethodKind Kind,
    bool HasResponsePayload,
    bool HasClientStreams,
    bool HasMethodTimeout,
    TimeSpan? MethodTimeout);

/// <summary>Represents a generated request with no business fields.</summary>
public readonly struct RpcEmptyRequest;

/// <summary>Codec for <see cref="RpcEmptyRequest"/>.</summary>
public sealed class RpcEmptyRequestCodec : IRpcCodec<RpcEmptyRequest>
{
    /// <summary>Gets the immutable codec instance.</summary>
    public static RpcEmptyRequestCodec Instance { get; } = new();

    private RpcEmptyRequestCodec()
    {
    }

    /// <inheritdoc />
    public void Serialize(in RpcEmptyRequest value, IBufferWriter<byte> buffer)
    {
    }

    /// <inheritdoc />
    public RpcEmptyRequest Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (!buffer.IsEmpty)
            throw new System.IO.InvalidDataException("An empty RPC request contains trailing data.");
        return default;
    }
}

/// <summary>Writes generated client streams after a request has been accepted.</summary>
public interface IRpcClientStreamWriter
{
    /// <summary>Writes all client streams owned by one invocation.</summary>
    /// <param name="sink">The selected connection-bound stream sink.</param>
    /// <param name="requestId">The owning request identifier.</param>
    /// <param name="cancellationToken">Stops local stream production.</param>
    ValueTask WriteAsync(
        IRpcClientStreamSink sink,
        long requestId,
        CancellationToken cancellationToken);
}

/// <summary>Sends typed client stream items on the connection selected for an invocation.</summary>
public interface IRpcClientStreamSink
{
    /// <summary>Sends one typed client stream and its completion frame.</summary>
    Task SendClientStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        CancellationToken cancellationToken = default);
}

/// <summary>Zero-allocation stream writer used by methods without client streams.</summary>
public readonly struct RpcNoClientStreams : IRpcClientStreamWriter
{
    /// <inheritdoc />
    public ValueTask WriteAsync(
        IRpcClientStreamSink sink,
        long requestId,
        CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

/// <summary>Client channel consumed by generated proxies.</summary>
public interface IRpcChannel : IRpcClientStreamSink
{
    /// <summary>Gets the runtime context owned by this channel.</summary>
    IRpcRuntimeContext RuntimeContext { get; }

    /// <summary>Invokes a unary RPC.</summary>
    ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Invokes a one-way RPC, optionally with generated client streams.</summary>
    ValueTask InvokeOneWayAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        in TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter;

    /// <summary>Invokes a client-streaming RPC.</summary>
    ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter;

    /// <summary>Invokes a server-streaming RPC.</summary>
    IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Invokes a duplex-streaming RPC.</summary>
    IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkCallOptions options,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter;
}
