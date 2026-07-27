namespace SharpLink.Sdk;

/// <summary>
/// Marks an RPC method as fire-and-forget. The method must return a non-generic
/// <see cref="System.Threading.Tasks.Task"/> or <see cref="System.Threading.Tasks.ValueTask"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OnewayAttribute : Attribute
{
}
