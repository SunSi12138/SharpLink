namespace SharpLink.Sdk;

/// <summary>
/// Marks a type as owned by an explicitly registered external Codec instead of the
/// SharpLink DTO generator.
/// </summary>
/// <example><code>[assembly: RpcExternalCodec(typeof(ThirdPartyGraph))]</code></example>
[AttributeUsage(
    AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = true)]
public sealed class RpcExternalCodecAttribute : Attribute
{
    /// <summary>Marks the attributed DTO type as externally serialized.</summary>
    public RpcExternalCodecAttribute()
    {
    }

    /// <summary>Marks an external or third-party type as externally serialized.</summary>
    /// <param name="type">The closed type handled by an explicitly registered Codec.</param>
    public RpcExternalCodecAttribute(Type type)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
    }

    /// <summary>Gets the assembly-level external type, when supplied.</summary>
    public Type? Type { get; }
}
