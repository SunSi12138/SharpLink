namespace SharpLink.Sdk;

/// <summary>Explicitly binds a closed RPC payload type to one concrete custom Codec implementation.</summary>
[AttributeUsage(
    AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = true,
    Inherited = false)]
public sealed class RpcCodecAttribute : Attribute
{
    /// <summary>Selects a custom Codec for the attributed type.</summary>
    public RpcCodecAttribute(Type codecType)
    {
        CodecType = codecType ?? throw new ArgumentNullException(nameof(codecType));
    }

    /// <summary>Selects a custom Codec for an external closed type at assembly scope.</summary>
    public RpcCodecAttribute(Type targetType, Type codecType)
    {
        TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
        CodecType = codecType ?? throw new ArgumentNullException(nameof(codecType));
    }

    /// <summary>Gets the assembly-level target type, when supplied.</summary>
    public Type? TargetType { get; }

    /// <summary>Gets the custom Codec implementation type.</summary>
    public Type CodecType { get; }
}
