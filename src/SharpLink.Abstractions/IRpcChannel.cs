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
public readonly record struct RpcMethodDescriptor
{
    private const byte HasResponsePayloadFlag = 1 << 0;
    private const byte HasClientStreamsFlag = 1 << 1;
    private const byte HasMethodTimeoutFlag = 1 << 2;
    private const byte IsIdempotentFlag = 1 << 3;
    private const byte ResponseNullableFlag = 1 << 4;

    /// <summary>Creates metadata for a generated RPC contract method.</summary>
    /// <param name="ContractId">Stable generated contract identifier.</param>
    /// <param name="MethodId">Stable generated method identifier.</param>
    /// <param name="Kind">Invocation shape.</param>
    /// <param name="HasResponsePayload">Whether a successful response contains a business payload.</param>
    /// <param name="HasClientStreams">Whether the request owns one or more client streams.</param>
    /// <param name="HasMethodTimeout">Whether the contract method declares <c>[Timeout]</c>.</param>
    /// <param name="MethodTimeout">Explicit method timeout, or <see langword="null"/> to use the client default.</param>
    /// <param name="IsIdempotent">Whether the contract explicitly permits idempotent retry policies.</param>
    /// <param name="ClientStreamCount">The number of generated client-stream parameters owned by the request.</param>
    /// <param name="ResponseNullable">Whether a successful reference-type response may be null.</param>
    public RpcMethodDescriptor(
        long ContractId,
        long MethodId,
        RpcMethodKind Kind,
        bool HasResponsePayload,
        bool HasClientStreams,
        bool HasMethodTimeout,
        TimeSpan? MethodTimeout,
        bool IsIdempotent = false,
        int ClientStreamCount = 0,
        bool ResponseNullable = false)
    {
        this.ContractId = ContractId;
        this.MethodId = MethodId;
        this.Kind = Kind;
        this.MethodTimeout = MethodTimeout;
        this.ClientStreamCount = ClientStreamCount;
        _flags = (byte)(
            (HasResponsePayload ? HasResponsePayloadFlag : 0) |
            (HasClientStreams ? HasClientStreamsFlag : 0) |
            (HasMethodTimeout ? HasMethodTimeoutFlag : 0) |
            (IsIdempotent ? IsIdempotentFlag : 0) |
            (ResponseNullable ? ResponseNullableFlag : 0));
    }

    /// <summary>Gets the stable generated contract identifier.</summary>
    public long ContractId { get; init; }
    /// <summary>Gets the stable generated method identifier.</summary>
    public long MethodId { get; init; }
    /// <summary>Gets the explicit method timeout, or <see langword="null"/> to use the client default.</summary>
    public TimeSpan? MethodTimeout { get; init; }
    /// <summary>Gets the number of client-stream parameters owned by the request.</summary>
    public int ClientStreamCount { get; init; }
    /// <summary>Gets the generated invocation shape.</summary>
    public RpcMethodKind Kind { get; init; }
    /// <summary>Gets whether a successful response contains a business payload.</summary>
    public bool HasResponsePayload
    {
        get => (_flags & HasResponsePayloadFlag) != 0;
        init => _flags = SetFlag(_flags, HasResponsePayloadFlag, value);
    }
    /// <summary>Gets whether the request owns one or more client streams.</summary>
    public bool HasClientStreams
    {
        get => (_flags & HasClientStreamsFlag) != 0;
        init => _flags = SetFlag(_flags, HasClientStreamsFlag, value);
    }
    /// <summary>Gets whether the contract method declares an explicit timeout.</summary>
    public bool HasMethodTimeout
    {
        get => (_flags & HasMethodTimeoutFlag) != 0;
        init => _flags = SetFlag(_flags, HasMethodTimeoutFlag, value);
    }
    /// <summary>Gets whether the contract permits idempotent retry policies.</summary>
    public bool IsIdempotent
    {
        get => (_flags & IsIdempotentFlag) != 0;
        init => _flags = SetFlag(_flags, IsIdempotentFlag, value);
    }
    /// <summary>Gets whether a successful reference-type response may be <see langword="null"/>.</summary>
    public bool ResponseNullable
    {
        get => (_flags & ResponseNullableFlag) != 0;
        init => _flags = SetFlag(_flags, ResponseNullableFlag, value);
    }

    private readonly byte _flags;

    private static byte SetFlag(byte flags, byte flag, bool value)
        => value ? (byte)(flags | flag) : (byte)(flags & ~flag);

    /// <summary>Deconstructs the descriptor without the response-nullability flag.</summary>
    /// <param name="ContractId">Receives the contract identifier.</param>
    /// <param name="MethodId">Receives the method identifier.</param>
    /// <param name="Kind">Receives the invocation shape.</param>
    /// <param name="HasResponsePayload">Receives whether a response payload exists.</param>
    /// <param name="HasClientStreams">Receives whether client streams exist.</param>
    /// <param name="HasMethodTimeout">Receives whether a method timeout was declared.</param>
    /// <param name="MethodTimeout">Receives the declared method timeout.</param>
    /// <param name="IsIdempotent">Receives whether idempotent retries are permitted.</param>
    /// <param name="ClientStreamCount">Receives the client stream count.</param>
    public void Deconstruct(
        out long ContractId,
        out long MethodId,
        out RpcMethodKind Kind,
        out bool HasResponsePayload,
        out bool HasClientStreams,
        out bool HasMethodTimeout,
        out TimeSpan? MethodTimeout,
        out bool IsIdempotent,
        out int ClientStreamCount)
    {
        ContractId = this.ContractId;
        MethodId = this.MethodId;
        Kind = this.Kind;
        HasResponsePayload = this.HasResponsePayload;
        HasClientStreams = this.HasClientStreams;
        HasMethodTimeout = this.HasMethodTimeout;
        MethodTimeout = this.MethodTimeout;
        IsIdempotent = this.IsIdempotent;
        ClientStreamCount = this.ClientStreamCount;
    }

    /// <summary>Deconstructs all descriptor values.</summary>
    /// <param name="ContractId">Receives the contract identifier.</param>
    /// <param name="MethodId">Receives the method identifier.</param>
    /// <param name="Kind">Receives the invocation shape.</param>
    /// <param name="HasResponsePayload">Receives whether a response payload exists.</param>
    /// <param name="HasClientStreams">Receives whether client streams exist.</param>
    /// <param name="HasMethodTimeout">Receives whether a method timeout was declared.</param>
    /// <param name="MethodTimeout">Receives the declared method timeout.</param>
    /// <param name="IsIdempotent">Receives whether idempotent retries are permitted.</param>
    /// <param name="ClientStreamCount">Receives the client stream count.</param>
    /// <param name="ResponseNullable">Receives whether a response may be <see langword="null"/>.</param>
    public void Deconstruct(
        out long ContractId,
        out long MethodId,
        out RpcMethodKind Kind,
        out bool HasResponsePayload,
        out bool HasClientStreams,
        out bool HasMethodTimeout,
        out TimeSpan? MethodTimeout,
        out bool IsIdempotent,
        out int ClientStreamCount,
        out bool ResponseNullable)
    {
        Deconstruct(
            out ContractId,
            out MethodId,
            out Kind,
            out HasResponsePayload,
            out HasClientStreams,
            out HasMethodTimeout,
            out MethodTimeout,
            out IsIdempotent,
            out ClientStreamCount);
        ResponseNullable = this.ResponseNullable;
    }
}

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
            throw RpcGeneratedCodecWire.DataLoss("An empty RPC request contains trailing data.");
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
