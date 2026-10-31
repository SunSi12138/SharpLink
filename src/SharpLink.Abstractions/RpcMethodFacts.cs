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

    private const uint Known = 1u << 16;
    private const uint Cancellable = 1u << 3;
    private const uint ResponsePayload = 1u << 4;
    private const uint NullableResponse = 1u << 5;
    private const uint DeclaredTimeout = 1u << 6;
    private const uint TimeoutValue = 1u << 7;
    private const uint Idempotent = 1u << 8;
    private const int StreamCountShift = 9;
    private readonly long _timeoutTicks;
    private readonly uint _flags;

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

        ContractId = contractId;
        MethodId = methodId;
        _timeoutTicks = methodTimeout.GetValueOrDefault().Ticks;
        _flags = Known | (uint)kind | ((uint)clientStreamCount << StreamCountShift)
            | (supportsCancellation ? Cancellable : 0)
            | (hasResponsePayload ? ResponsePayload : 0)
            | (responseNullable ? NullableResponse : 0)
            | (hasMethodTimeout ? DeclaredTimeout : 0)
            | (methodTimeout.HasValue ? TimeoutValue : 0)
            | (isIdempotent ? Idempotent : 0);
    }

    /// <summary>Gets whether this result describes a known method.</summary>
    public bool IsKnown => (_flags & Known) != 0;
    /// <summary>Gets the original contract identifier.</summary>
    public long ContractId { get; }
    /// <summary>Gets the original method identifier.</summary>
    public long MethodId { get; }
    /// <summary>Gets the invocation shape.</summary>
    public RpcMethodKind Kind => (RpcMethodKind)(_flags & 7);
    /// <summary>Gets the exact client-stream parameter count.</summary>
    public int ClientStreamCount => (int)((_flags >> StreamCountShift) & 127);
    /// <summary>Gets whether the method owns client streams.</summary>
    public bool HasClientStreams => ClientStreamCount != 0;
    /// <summary>Gets whether the method consumes cancellation or owns streams.</summary>
    public bool SupportsCancellation => (_flags & Cancellable) != 0;
    /// <summary>Gets whether a successful response carries a payload.</summary>
    public bool HasResponsePayload => (_flags & ResponsePayload) != 0;
    /// <summary>Gets whether the response payload can be null.</summary>
    public bool ResponseNullable => (_flags & NullableResponse) != 0;
    /// <summary>Gets whether the method declares a timeout policy.</summary>
    public bool HasMethodTimeout => (_flags & DeclaredTimeout) != 0;
    /// <summary>Gets the complete declared timeout without another method lookup.</summary>
    public TimeSpan? MethodTimeout => (_flags & TimeoutValue) != 0 ? TimeSpan.FromTicks(_timeoutTicks) : null;
    /// <summary>Gets whether the method declares idempotency.</summary>
    public bool IsIdempotent => (_flags & Idempotent) != 0;

    /// <summary>Projects the already-resolved scalar facts into the public observer descriptor.</summary>
    public RpcMethodDescriptor ToDescriptor()
    {
        if (!IsKnown)
            throw new InvalidOperationException("An unknown method has no descriptor.");
        return new RpcMethodDescriptor(ContractId, MethodId, Kind, HasResponsePayload,
            HasClientStreams, HasMethodTimeout, MethodTimeout, IsIdempotent,
            ClientStreamCount, ResponseNullable);
    }
}
