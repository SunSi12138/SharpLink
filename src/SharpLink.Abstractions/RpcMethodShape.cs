namespace SharpLink.Abstractions;

/// <summary>
/// Packs every compile-time fact the RPC hot paths need about one generated method into a
/// single 32-bit word, so one generated resolution call can replace repeated
/// <see cref="RpcMethodDescriptor"/> materialization.
/// </summary>
/// <remarks>
/// The default value is invalid and must be rejected by consumers. Only a typed construction
/// describes a known method; <see cref="UnknownMethod"/> explicitly describes an absent identifier.
/// The compact representation is private and is not a generated-code ABI.
/// </remarks>
public readonly struct RpcMethodShape : IEquatable<RpcMethodShape>
{
    /// <summary>The maximum supported number of generated client-stream parameters.</summary>
    public const int MaxClientStreamCount = 127;

    private const uint ResolutionMask = 0b11u;
    private const uint ResolutionUnknownMethod = 1u;
    private const uint ResolutionKnown = 2u;
    private const int KindShift = 2;
    private const uint KindMask = 0b111u << KindShift;
    private const int ClientStreamCountShift = 5;
    private const uint ClientStreamCountMask = 0b1111111u << ClientStreamCountShift;
    private const int SupportsCancellationShift = 12;
    private const int HasResponsePayloadShift = 13;
    private const int ResponseNullableShift = 14;
    private const int HasMethodTimeoutShift = 15;
    private const int IsIdempotentShift = 16;
    private const int HasMethodTimeoutValueShift = 17;

    private readonly uint _packed;

    private RpcMethodShape(uint packed) => _packed = packed;

    /// <summary>Creates a known shape from its individual facts.</summary>
    /// <param name="kind">The generated invocation shape.</param>
    /// <param name="clientStreamCount">The number of generated client-stream parameters.</param>
    /// <param name="supportsCancellation">Whether the server must create per-call cancellation state.</param>
    /// <param name="hasResponsePayload">Whether a successful response contains a business payload.</param>
    /// <param name="responseNullable">Whether a successful reference-type response may be null.</param>
    /// <param name="hasMethodTimeout">Whether the contract declares <c>[Timeout]</c>.</param>
    /// <param name="isIdempotent">Whether the contract explicitly permits idempotent retry policies.</param>
    /// <param name="hasMethodTimeoutValue">Whether the declared timeout carries a usable value.</param>
    public RpcMethodShape(
        RpcMethodKind kind,
        int clientStreamCount,
        bool supportsCancellation,
        bool hasResponsePayload,
        bool responseNullable = false,
        bool hasMethodTimeout = false,
        bool isIdempotent = false,
        bool hasMethodTimeoutValue = false)
    {
        if (kind is < RpcMethodKind.Unary or > RpcMethodKind.DuplexStreaming)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown RPC method kind.");
        if ((uint)clientStreamCount > MaxClientStreamCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientStreamCount),
                clientStreamCount,
                $"A generated method cannot own more than {MaxClientStreamCount} client streams.");
        }

        var requiresClientStreams = kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming;
        if ((requiresClientStreams && clientStreamCount == 0) ||
            ((kind is RpcMethodKind.Unary or RpcMethodKind.ServerStreaming) && clientStreamCount != 0))
            throw new ArgumentException("Client-stream count does not match the method kind.", nameof(clientStreamCount));
        var streamsResponse = kind is RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming;
        if ((clientStreamCount != 0 || streamsResponse) && !supportsCancellation)
            throw new ArgumentException("Streaming methods require cancellation support.", nameof(supportsCancellation));
        if ((kind == RpcMethodKind.OneWay && hasResponsePayload) || (streamsResponse && !hasResponsePayload))
            throw new ArgumentException("Response payload does not match the method kind.", nameof(hasResponsePayload));
        if (responseNullable && !hasResponsePayload)
            throw new ArgumentException("Only response payloads can be nullable.", nameof(responseNullable));
        if (hasMethodTimeoutValue && !hasMethodTimeout)
            throw new ArgumentException("A timeout value requires a declared timeout.", nameof(hasMethodTimeoutValue));

        _packed = ResolutionKnown
            | (((uint)kind & 0b111u) << KindShift)
            | (((uint)clientStreamCount & 0b1111111u) << ClientStreamCountShift)
            | (supportsCancellation ? 1u << SupportsCancellationShift : 0u)
            | (hasResponsePayload ? 1u << HasResponsePayloadShift : 0u)
            | (responseNullable ? 1u << ResponseNullableShift : 0u)
            | (hasMethodTimeout ? 1u << HasMethodTimeoutShift : 0u)
            | (isIdempotent ? 1u << IsIdempotentShift : 0u)
            | (hasMethodTimeoutValue ? 1u << HasMethodTimeoutValueShift : 0u);
    }

    /// <summary>Gets the shape reported for a method identifier that is not part of the contract.</summary>
    public static RpcMethodShape UnknownMethod => new(ResolutionUnknownMethod);

    /// <summary>Gets whether the shape describes a method that exists in the contract.</summary>
    public bool IsKnown => (_packed & ResolutionMask) == ResolutionKnown;

    /// <summary>Gets whether this is an invalid, zero-initialized shape.</summary>
    public bool IsInvalid => !IsKnown && !IsUnknownMethod;

    /// <summary>Gets whether the resolved method identifier is outside the contract.</summary>
    public bool IsUnknownMethod => (_packed & ResolutionMask) == ResolutionUnknownMethod;

    /// <summary>Gets the generated invocation shape.</summary>
    public RpcMethodKind Kind => (RpcMethodKind)((_packed & KindMask) >> KindShift);

    /// <summary>Gets the number of generated client-stream parameters.</summary>
    public int ClientStreamCount => (int)((_packed & ClientStreamCountMask) >> ClientStreamCountShift);

    /// <summary>Gets whether the server must create per-call cancellation state.</summary>
    public bool SupportsCancellation => (_packed & (1u << SupportsCancellationShift)) != 0;

    /// <summary>Gets whether a successful response contains a business payload.</summary>
    public bool HasResponsePayload => (_packed & (1u << HasResponsePayloadShift)) != 0;

    /// <summary>Gets whether a successful reference-type response may be null.</summary>
    public bool ResponseNullable => (_packed & (1u << ResponseNullableShift)) != 0;

    /// <summary>Gets whether the contract method declares an explicit timeout.</summary>
    public bool HasMethodTimeout => (_packed & (1u << HasMethodTimeoutShift)) != 0;

    /// <summary>Gets whether the declared timeout carries a usable value.</summary>
    public bool HasMethodTimeoutValue => (_packed & (1u << HasMethodTimeoutValueShift)) != 0;

    /// <summary>Gets whether the contract permits idempotent retry policies.</summary>
    public bool IsIdempotent => (_packed & (1u << IsIdempotentShift)) != 0;

    /// <summary>Gets whether the generated method owns at least one client stream.</summary>
    public bool HasClientStreams => ClientStreamCount != 0;

    /// <inheritdoc/>
    public bool Equals(RpcMethodShape other) => _packed == other._packed;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RpcMethodShape other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (int)_packed;

    /// <inheritdoc/>
    public override string ToString() => IsInvalid
        ? "RpcMethodShape(Invalid)"
        : IsUnknownMethod
            ? "RpcMethodShape(UnknownMethod)"
            : $"RpcMethodShape({Kind}, streams: {ClientStreamCount}, cancellable: {SupportsCancellation})";

    /// <summary>Compares two shapes for equality.</summary>
    /// <param name="left">The left shape.</param>
    /// <param name="right">The right shape.</param>
    /// <returns><see langword="true"/> when both shapes are identical.</returns>
    public static bool operator ==(RpcMethodShape left, RpcMethodShape right) => left._packed == right._packed;

    /// <summary>Compares two shapes for inequality.</summary>
    /// <param name="left">The left shape.</param>
    /// <param name="right">The right shape.</param>
    /// <returns><see langword="true"/> when the shapes differ.</returns>
    public static bool operator !=(RpcMethodShape left, RpcMethodShape right) => left._packed != right._packed;
}
