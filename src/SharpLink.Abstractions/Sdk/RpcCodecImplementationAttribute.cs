namespace SharpLink.Sdk;

/// <summary>Declares the stable wire-format and schema identity of a hand-written RPC Codec implementation.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class RpcCodecImplementationAttribute : Attribute
{
    /// <summary>Creates a custom Codec implementation identity.</summary>
    public RpcCodecImplementationAttribute(string wireFormatId, string schemaId)
    {
        WireFormatId = wireFormatId ?? throw new ArgumentNullException(nameof(wireFormatId));
        SchemaId = schemaId ?? throw new ArgumentNullException(nameof(schemaId));
    }

    /// <summary>Gets the stable binary wire-format identity.</summary>
    public string WireFormatId { get; }

    /// <summary>Gets the deterministic payload schema identity.</summary>
    public string SchemaId { get; }
}
