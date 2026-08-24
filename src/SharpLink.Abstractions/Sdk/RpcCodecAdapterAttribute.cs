namespace SharpLink.Sdk;

/// <summary>Explicitly selects a registered Codec adapter or direct Codec for a closed RPC payload type.</summary>
[AttributeUsage(
    AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = true,
    Inherited = false)]
public sealed class RpcCodecAdapterAttribute : Attribute
{
    /// <summary>Selects an adapter or direct Codec for the attributed type.</summary>
    public RpcCodecAdapterAttribute(Type adapterType)
    {
        AdapterType = adapterType ?? throw new ArgumentNullException(nameof(adapterType));
    }

    /// <summary>Selects an adapter or direct Codec for an external closed type at assembly scope.</summary>
    public RpcCodecAdapterAttribute(Type targetType, Type adapterType)
    {
        TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
        AdapterType = adapterType ?? throw new ArgumentNullException(nameof(adapterType));
    }

    /// <summary>Gets the assembly-level target type, when supplied.</summary>
    public Type? TargetType { get; }

    /// <summary>Gets the selected adapter or direct Codec implementation type.</summary>
    public Type AdapterType { get; }

    /// <summary>Gets or sets the stable wire-format identity for a direct IRpcCodec&lt;T&gt; binding.</summary>
    public string? WireFormatId { get; set; }
}
