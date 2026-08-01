namespace SharpLink.Sdk;

/// <summary>Marks a concrete class for generated RPC service registration.</summary>
/// <remarks>
/// A service must implement exactly one generated RPC contract. Its constructor and configured
/// <see cref="Lifetime"/> are validated at compile time.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RpcServiceAttribute : Attribute
{
    /// <summary>Gets or initializes the lifetime of the generated RPC service.</summary>
    public SharpLinkServiceLifetime Lifetime { get; init; } = SharpLinkServiceLifetime.Singleton;
}
