namespace SharpLink.Sdk;

/// <summary>Requires a DTO member to be present while deserializing.</summary>
/// <example><code>[RpcRequired] public string Name { get; init; } = string.Empty;</code></example>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class RpcRequiredAttribute : Attribute;
