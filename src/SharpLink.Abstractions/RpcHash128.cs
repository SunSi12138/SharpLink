using System.Globalization;

namespace SharpLink.Abstractions;

/// <summary>Represents a deterministic fixed-width RPC semantic identity.</summary>
public readonly struct RpcHash128 : IEquatable<RpcHash128>
{
    /// <summary>Creates a 128-bit identity from its high and low 64-bit words.</summary>
    public RpcHash128(ulong high, ulong low)
    {
        High = high;
        Low = low;
    }

    /// <summary>Gets the high 64 bits.</summary>
    public ulong High { get; }

    /// <summary>Gets the low 64 bits.</summary>
    public ulong Low { get; }

    /// <summary>Gets whether all bits are zero.</summary>
    public bool IsEmpty => (High | Low) == 0;

    /// <inheritdoc />
    public bool Equals(RpcHash128 other) => High == other.High && Low == other.Low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RpcHash128 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(High, Low);

    /// <inheritdoc />
    public override string ToString()
        => High.ToString("x16", CultureInfo.InvariantCulture) +
           Low.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Compares two RPC identities for exact equality.</summary>
    public static bool operator ==(RpcHash128 left, RpcHash128 right) => left.Equals(right);

    /// <summary>Compares two RPC identities for inequality.</summary>
    public static bool operator !=(RpcHash128 left, RpcHash128 right) => !left.Equals(right);
}
