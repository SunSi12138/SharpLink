namespace SharpLink.Sdk;

/// <summary>Explicitly includes a DTO entry point in SharpLink source-generated serialization.</summary>
/// <example><code>[RpcSerializable] public sealed class WorkOrder { }</code></example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class RpcSerializableAttribute : Attribute;
