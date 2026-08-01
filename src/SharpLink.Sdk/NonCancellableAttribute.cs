namespace SharpLink.Sdk;

/// <summary>
/// Declares that an RPC method intentionally does not accept a <see cref="System.Threading.CancellationToken"/>.
/// </summary>
/// <remarks>
/// Client cancellation and deadlines still stop the caller from waiting. The server observes the
/// invocation to completion and suppresses any abandoned late response, but application work that
/// does not accept a cancellation token may continue in the background. Streaming framework pumps,
/// dispatchers, and flow-control waits remain cancellable so that abandoning a stream does not retain
/// connection resources.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class NonCancellableAttribute : Attribute;
