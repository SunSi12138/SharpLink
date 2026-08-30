namespace SharpLink.Sdk;

/// <summary>
/// Declares the fixed-width semantic identity of an opaque hand-written Codec or Codec Adapter.
/// Change this value whenever the implementation's RPC-visible wire semantics change.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class RpcCodecSemanticIdentityAttribute : Attribute
{
    /// <summary>Creates one opaque serializer semantic identity.</summary>
    public RpcCodecSemanticIdentityAttribute(ulong high, ulong low)
    {
        High = high;
        Low = low;
    }

    /// <summary>Gets the high 64 bits of the semantic identity.</summary>
    public ulong High { get; }

    /// <summary>Gets the low 64 bits of the semantic identity.</summary>
    public ulong Low { get; }
}
