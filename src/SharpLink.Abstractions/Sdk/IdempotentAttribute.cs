namespace SharpLink.Sdk;

/// <summary>Marks a unary RPC as safe for an explicitly configured retry policy.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class IdempotentAttribute : Attribute
{
}
