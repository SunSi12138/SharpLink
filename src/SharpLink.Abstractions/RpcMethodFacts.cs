namespace SharpLink.Abstractions;

/// <summary>
/// Immutable scalar facts for one generated method, resolved once and reused throughout a call.
/// </summary>
/// <remarks>
/// Generated stubs keep one instance per method. The object contains no registration, stub,
/// codec, Type, Assembly or delegate reference, so retaining it does not retain a module generation.
/// </remarks>
public sealed class RpcMethodFacts
{
    /// <summary>Maximum number of client-stream parameters in a generated method.</summary>
    public const int MaxClientStreamCount = 127;

    private const byte Known = 1 << 0;
    private const byte Cancellable = 1 << 1;
    private readonly RpcMethodDescriptor _descriptor;
    private readonly byte _flags;

    private RpcMethodFacts() { }

    /// <summary>Gets the explicit result for an identifier absent from the contract.</summary>
    public static RpcMethodFacts UnknownMethod { get; } = new();

    internal static RpcMethodFacts Invalid { get; } = new();

    /// <summary>Creates complete facts for a known method.</summary>
    /// <param name="contractId">The generated contract identifier.</param>
    /// <param name="methodId">The generated method identifier.</param>
    /// <param name="kind">The invocation shape.</param>
    /// <param name="clientStreamCount">The exact number of client-stream parameters.</param>
    /// <param name="supportsCancellation">Whether the method consumes cancellation or owns streams.</param>
    /// <param name="hasResponsePayload">Whether the response contains a payload.</param>
    /// <param name="responseNullable">Whether a response payload can be null.</param>
    /// <param name="hasMethodTimeout">Whether the method declares a timeout policy.</param>
    /// <param name="methodTimeout">The complete declared timeout value, when present.</param>
    /// <param name="isIdempotent">Whether the method declares idempotency.</param>
    public RpcMethodFacts(
        long contractId,
        long methodId,
        RpcMethodKind kind,
        int clientStreamCount,
        bool supportsCancellation,
        bool hasResponsePayload,
        bool responseNullable = false,
        bool hasMethodTimeout = false,
        TimeSpan? methodTimeout = null,
        bool isIdempotent = false)
    {
        if (kind is < RpcMethodKind.Unary or > RpcMethodKind.DuplexStreaming)
            throw new ArgumentOutOfRangeException(nameof(kind));
        if ((uint)clientStreamCount > MaxClientStreamCount)
            throw new ArgumentOutOfRangeException(nameof(clientStreamCount));
        if ((kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming) && clientStreamCount == 0 ||
            (kind is RpcMethodKind.Unary or RpcMethodKind.ServerStreaming) && clientStreamCount != 0)
            throw new ArgumentException("Client-stream count does not match the method kind.", nameof(clientStreamCount));
        var streamsResponse = kind is RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming;
        if ((clientStreamCount != 0 || streamsResponse) && !supportsCancellation)
            throw new ArgumentException("Streaming methods require cancellation support.", nameof(supportsCancellation));
        if (kind == RpcMethodKind.OneWay && hasResponsePayload || streamsResponse && !hasResponsePayload)
            throw new ArgumentException("Response payload does not match the method kind.", nameof(hasResponsePayload));
        if (responseNullable && !hasResponsePayload)
            throw new ArgumentException("Only response payloads can be nullable.", nameof(responseNullable));
        if (methodTimeout.HasValue && !hasMethodTimeout)
            throw new ArgumentException("A timeout value requires a declared timeout.", nameof(methodTimeout));

        _descriptor = new RpcMethodDescriptor(contractId, methodId, kind, hasResponsePayload,
            clientStreamCount != 0, hasMethodTimeout, methodTimeout, isIdempotent,
            clientStreamCount, responseNullable);
        _flags = (byte)(Known | (supportsCancellation ? Cancellable : 0));
    }

    /// <summary>Gets whether this result describes a known method.</summary>
    public bool IsKnown => (_flags & Known) != 0;
    /// <summary>Gets the original contract identifier.</summary>
    public long ContractId => _descriptor.ContractId;
    /// <summary>Gets the original method identifier.</summary>
    public long MethodId => _descriptor.MethodId;
    /// <summary>Gets the invocation shape.</summary>
    public RpcMethodKind Kind => _descriptor.Kind;
    /// <summary>Gets the exact client-stream parameter count.</summary>
    public int ClientStreamCount => _descriptor.ClientStreamCount;
    /// <summary>Gets whether the method owns client streams.</summary>
    public bool HasClientStreams => _descriptor.HasClientStreams;
    /// <summary>Gets whether the method consumes cancellation or owns streams.</summary>
    public bool SupportsCancellation => (_flags & Cancellable) != 0;
    /// <summary>Gets whether a successful response carries a payload.</summary>
    public bool HasResponsePayload => _descriptor.HasResponsePayload;
    /// <summary>Gets whether the response payload can be null.</summary>
    public bool ResponseNullable => _descriptor.ResponseNullable;
    /// <summary>Gets whether the method declares a timeout policy.</summary>
    public bool HasMethodTimeout => _descriptor.HasMethodTimeout;
    /// <summary>Gets the complete declared timeout without another method lookup.</summary>
    public TimeSpan? MethodTimeout => _descriptor.MethodTimeout;
    /// <summary>Gets whether the method declares idempotency.</summary>
    public bool IsIdempotent => _descriptor.IsIdempotent;

    /// <summary>Projects the already-resolved scalar facts into the public observer descriptor.</summary>
    public RpcMethodDescriptor ToDescriptor()
    {
        if (!IsKnown)
            throw new InvalidOperationException("An unknown method has no descriptor.");
        return _descriptor;
    }
}
