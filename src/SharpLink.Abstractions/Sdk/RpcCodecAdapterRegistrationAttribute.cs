namespace SharpLink.Sdk;

/// <summary>Declares one serializer Codec adapter for source-generated selection and lifecycle ownership.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RpcCodecAdapterRegistrationAttribute : Attribute
{
    /// <summary>Creates an adapter registration.</summary>
    public RpcCodecAdapterRegistrationAttribute(Type adapterType, string adapterId)
    {
        AdapterType = adapterType ?? throw new ArgumentNullException(nameof(adapterType));
        AdapterId = adapterId ?? throw new ArgumentNullException(nameof(adapterId));
    }

    /// <summary>Gets the public adapter implementation type.</summary>
    public Type AdapterType { get; }

    /// <summary>Gets the implementation and lifecycle identity.</summary>
    public string AdapterId { get; }

    /// <summary>Gets or initializes the serializer attribute that selects this adapter.</summary>
    public Type? SelectorAttributeType { get; init; }
}
