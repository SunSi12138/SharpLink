using System.Runtime.CompilerServices;

namespace SharpLink.Abstractions;

/// <summary>
/// Packs every compile-time fact the RPC hot paths need about one generated method into a
/// single 32-bit word, so one generated resolution call can replace repeated
/// <see cref="RpcMethodDescriptor"/> materialization.
/// </summary>
/// <remarks>
/// <para>
/// The default value is deliberately conservative: <see cref="Unresolvable"/> means "this stub
/// cannot describe its methods", which keeps <see cref="SupportsCancellation"/> <see langword="true"/>
/// and reports an unknown client-stream count. A zero-initialized shape therefore preserves the
/// legacy fail-safe behavior instead of silently dropping per-call cancellation state.
/// </para>
/// <para>Bit layout of the packed word:</para>
/// <list type="table">
/// <item><description>bits 0-1: resolution (0 unresolvable, 1 unknown method, 2 known);</description></item>
/// <item><description>bits 2-4: <see cref="RpcMethodKind"/>;</description></item>
/// <item><description>bits 5-8: client-stream count, or <see cref="UnknownClientStreamCount"/>;</description></item>
/// <item><description>bit 9: <see cref="SupportsCancellation"/>;</description></item>
/// <item><description>bit 10: <see cref="HasResponsePayload"/>;</description></item>
/// <item><description>bit 11: <see cref="ResponseNullable"/>;</description></item>
/// <item><description>bit 12: <see cref="HasMethodTimeout"/>;</description></item>
/// <item><description>bit 13: <see cref="IsIdempotent"/>;</description></item>
/// <item><description>bit 14: <see cref="HasMethodTimeoutValue"/>;</description></item>
/// <item><description>bits 16-23: <see cref="TimeoutOrdinal"/> into the contract timeout table.</description></item>
/// </list>
/// </remarks>
public readonly struct RpcMethodShape : IEquatable<RpcMethodShape>
{
    /// <summary>The client-stream count reported when a stub cannot describe its methods.</summary>
    public const int UnknownClientStreamCount = 0b1111;

    private const uint ResolutionMask = 0b11u;
    private const uint ResolutionUnknownMethod = 1u;
    private const uint ResolutionKnown = 2u;
    private const int KindShift = 2;
    private const uint KindMask = 0b111u << KindShift;
    private const int ClientStreamCountShift = 5;
    private const uint ClientStreamCountMask = 0b1111u << ClientStreamCountShift;
    private const int SupportsCancellationShift = 9;
    private const int HasResponsePayloadShift = 10;
    private const int ResponseNullableShift = 11;
    private const int HasMethodTimeoutShift = 12;
    private const int IsIdempotentShift = 13;
    private const int HasMethodTimeoutValueShift = 14;
    private const int TimeoutOrdinalShift = 16;
    private const uint TimeoutOrdinalMask = 0xFFu << TimeoutOrdinalShift;

    private readonly uint _packed;

    /// <summary>Creates a shape from its packed representation.</summary>
    /// <param name="packed">The packed word produced by the source generator.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RpcMethodShape(uint packed) => _packed = packed;

    /// <summary>Creates a known shape from its individual facts.</summary>
    /// <param name="kind">The generated invocation shape.</param>
    /// <param name="clientStreamCount">The number of generated client-stream parameters.</param>
    /// <param name="supportsCancellation">Whether the server must create per-call cancellation state.</param>
    /// <param name="hasResponsePayload">Whether a successful response contains a business payload.</param>
    /// <param name="responseNullable">Whether a successful reference-type response may be null.</param>
    /// <param name="hasMethodTimeout">Whether the contract declares <c>[Timeout]</c>.</param>
    /// <param name="isIdempotent">Whether the contract explicitly permits idempotent retry policies.</param>
    /// <param name="hasMethodTimeoutValue">Whether the declared timeout carries a usable value.</param>
    /// <param name="timeoutOrdinal">The ordinal of the declared timeout in the contract timeout table.</param>
    public RpcMethodShape(
        RpcMethodKind kind,
        int clientStreamCount,
        bool supportsCancellation,
        bool hasResponsePayload,
        bool responseNullable = false,
        bool hasMethodTimeout = false,
        bool isIdempotent = false,
        bool hasMethodTimeoutValue = false,
        int timeoutOrdinal = 0)
    {
        if ((uint)clientStreamCount > UnknownClientStreamCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientStreamCount),
                clientStreamCount,
                $"A generated method cannot own more than {UnknownClientStreamCount} client streams.");
        }
        if (hasMethodTimeout && (uint)timeoutOrdinal > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutOrdinal),
                timeoutOrdinal,
                "A contract cannot declare more than 256 distinct method timeouts.");
        }

        _packed = ResolutionKnown
            | (((uint)kind & 0b111u) << KindShift)
            | (((uint)clientStreamCount & 0b1111u) << ClientStreamCountShift)
            | (supportsCancellation ? 1u << SupportsCancellationShift : 0u)
            | (hasResponsePayload ? 1u << HasResponsePayloadShift : 0u)
            | (responseNullable ? 1u << ResponseNullableShift : 0u)
            | (hasMethodTimeout ? 1u << HasMethodTimeoutShift : 0u)
            | (isIdempotent ? 1u << IsIdempotentShift : 0u)
            | (hasMethodTimeoutValue ? 1u << HasMethodTimeoutValueShift : 0u)
            | ((uint)timeoutOrdinal << TimeoutOrdinalShift);
    }

    /// <summary>
    /// Gets the conservative shape reported by a stub that cannot describe its methods; the
    /// default value of this type.
    /// </summary>
    public static RpcMethodShape Unresolvable => default;

    /// <summary>Gets the shape reported for a method identifier that is not part of the contract.</summary>
    public static RpcMethodShape UnknownMethod => new(ResolutionUnknownMethod);

    /// <summary>Gets the packed representation of this shape.</summary>
    public uint Packed => _packed;

    /// <summary>Gets whether the shape describes a method that exists in the contract.</summary>
    public bool IsKnown => (_packed & ResolutionMask) == ResolutionKnown;

    /// <summary>Gets whether the stub could not describe the method at all.</summary>
    public bool IsUnresolvable => (_packed & ResolutionMask) == 0u;

    /// <summary>Gets whether the resolved method identifier is outside the contract.</summary>
    public bool IsUnknownMethod => (_packed & ResolutionMask) == ResolutionUnknownMethod;

    /// <summary>Gets the generated invocation shape.</summary>
    public RpcMethodKind Kind => (RpcMethodKind)((_packed & KindMask) >> KindShift);

    /// <summary>
    /// Gets the number of generated client-stream parameters, or
    /// <see cref="UnknownClientStreamCount"/> when the stub cannot describe the method.
    /// </summary>
    public int ClientStreamCount => IsUnresolvable
        ? UnknownClientStreamCount
        : (int)((_packed & ClientStreamCountMask) >> ClientStreamCountShift);

    /// <summary>Gets whether the client-stream count is meaningful.</summary>
    public bool HasKnownClientStreamCount => !IsUnresolvable;

    /// <summary>
    /// Gets whether the server must create per-call cancellation state. An unresolvable shape
    /// answers <see langword="true"/> so that custom stubs keep the legacy fail-safe behavior.
    /// </summary>
    public bool SupportsCancellation =>
        IsUnresolvable || (_packed & (1u << SupportsCancellationShift)) != 0;

    /// <summary>Gets whether a successful response contains a business payload.</summary>
    public bool HasResponsePayload => (_packed & (1u << HasResponsePayloadShift)) != 0;

    /// <summary>Gets whether a successful reference-type response may be null.</summary>
    public bool ResponseNullable => (_packed & (1u << ResponseNullableShift)) != 0;

    /// <summary>Gets whether the contract method declares an explicit timeout.</summary>
    public bool HasMethodTimeout => (_packed & (1u << HasMethodTimeoutShift)) != 0;

    /// <summary>Gets whether <see cref="TimeoutOrdinal"/> addresses a generated timeout value.</summary>
    public bool HasMethodTimeoutValue => (_packed & (1u << HasMethodTimeoutValueShift)) != 0;

    /// <summary>Gets whether the contract permits idempotent retry policies.</summary>
    public bool IsIdempotent => (_packed & (1u << IsIdempotentShift)) != 0;

    /// <summary>Gets the ordinal of the declared timeout in the contract timeout table.</summary>
    public int TimeoutOrdinal => (int)((_packed & TimeoutOrdinalMask) >> TimeoutOrdinalShift);

    /// <summary>Gets whether the generated method owns at least one client stream.</summary>
    public bool HasClientStreams => ClientStreamCount != 0;

    /// <inheritdoc/>
    public bool Equals(RpcMethodShape other) => _packed == other._packed;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RpcMethodShape other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (int)_packed;

    /// <inheritdoc/>
    public override string ToString() => IsUnresolvable
        ? "RpcMethodShape(Unresolvable)"
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
