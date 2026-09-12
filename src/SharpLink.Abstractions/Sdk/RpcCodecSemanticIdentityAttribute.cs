namespace SharpLink.Sdk;

/// <summary>
/// Declares the fixed-width semantic identity of an opaque hand-written Codec or Codec Adapter.
/// Change this value whenever the implementation's RPC-visible wire semantics change.
/// For Codec Adapters, SharpLink combines this value with the target type's stable logical identity;
/// SharpLink does not infer serializer-specific schema evolution inside the same target type, so the
/// adapter or integration author must change this identity when that closed Codec's wire schema changes.
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
