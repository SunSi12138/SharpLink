namespace SharpLink.Sdk;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RpcServiceAttribute : Attribute
{
    /// <summary>Gets or initializes the lifetime of the generated RPC service.</summary>
    public SharpLinkServiceLifetime Lifetime { get; init; } = SharpLinkServiceLifetime.Singleton;
}
