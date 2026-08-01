namespace SharpLink.Sdk;

/// <summary>Excludes a public DTO field or property from generated serialization.</summary>
/// <example><code>[RpcIgnore] public string LocalCacheKey { get; set; } = string.Empty;</code></example>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class RpcIgnoreAttribute : Attribute;
