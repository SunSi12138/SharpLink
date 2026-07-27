namespace SharpLink.Sdk;

/// <summary>Marks a publicly reachable interface whose supported instance methods are RPC routes.</summary>
/// <remarks>Abstract properties, indexers, events, static methods, and by-reference signatures are not RPC routes and produce compile-time diagnostics.</remarks>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class RpcContractAttribute : Attribute
{
}
