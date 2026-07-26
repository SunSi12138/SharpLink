namespace SharpLink.Sdk;

/// <summary>Declares the compile-time identity of a serializer Codec adapter.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RpcCodecAdapterRegistrationAttribute : Attribute
{
    /// <summary>Creates an adapter registration.</summary>
    public RpcCodecAdapterRegistrationAttribute(Type adapterType, string adapterId, string wireFormatId)
    {
        AdapterType = adapterType ?? throw new ArgumentNullException(nameof(adapterType));
        AdapterId = adapterId ?? throw new ArgumentNullException(nameof(adapterId));
        WireFormatId = wireFormatId ?? throw new ArgumentNullException(nameof(wireFormatId));
    }

    /// <summary>Gets the public adapter implementation type.</summary>
    public Type AdapterType { get; }

    /// <summary>Gets the implementation and lifecycle identity.</summary>
    public string AdapterId { get; }

    /// <summary>Gets the stable binary wire-format identity.</summary>
    public string WireFormatId { get; }

    /// <summary>Gets or initializes the serializer attribute that selects this adapter.</summary>
    public Type? SelectorAttributeType { get; init; }
}
