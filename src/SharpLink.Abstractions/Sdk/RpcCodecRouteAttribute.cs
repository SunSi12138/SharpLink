namespace SharpLink.Sdk;

/// <summary>Routes an RPC payload scope to a registered Codec adapter at compile time.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class RpcCodecRouteAttribute : Attribute
{
    /// <summary>Creates an assembly-level Codec route.</summary>
    public RpcCodecRouteAttribute(RpcCodecScope scope, Type adapterType)
    {
        Scope = scope;
        AdapterType = adapterType ?? throw new ArgumentNullException(nameof(adapterType));
    }

    /// <summary>Gets the payload scope selected by this route.</summary>
    public RpcCodecScope Scope { get; }

    /// <summary>Gets the registered Codec adapter implementation type.</summary>
    public Type AdapterType { get; }
}
