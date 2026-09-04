namespace SharpLink.Abstractions;

/// <summary>
/// Publishes the deterministic default Codec identity of one closed payload type for downstream
/// source-generation. The target <see cref="Type"/> is a metadata lookup key and is not part of the hash.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class SharpLinkGeneratedCodecIdentityAttribute : Attribute
{
    /// <summary>Creates one generated Codec identity entry.</summary>
    public SharpLinkGeneratedCodecIdentityAttribute(Type targetType, ulong hashHigh, ulong hashLow)
    {
        TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
        CodecHash = new RpcHash128(hashHigh, hashLow);
    }

    /// <summary>Gets the closed payload type used to locate this generated identity.</summary>
    public Type TargetType { get; }

    /// <summary>Gets the deterministic default Codec identity.</summary>
    public RpcHash128 CodecHash { get; }
}
