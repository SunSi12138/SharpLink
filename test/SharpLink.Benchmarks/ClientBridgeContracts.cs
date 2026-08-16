using System.Collections.Generic;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

/// <summary>
/// Contract dedicated to issue #159's client-proxy bridge comparison. Each method returns a
/// <c>ValueTask&lt;T&gt;</c>, which the generated proxy passes through unchanged (Variant C). The
/// benchmark then applies the two <c>Task</c>-producing bridges over that single operation:
/// Variant A (<c>.AsTask()</c>) and Variant B (<c>async Task</c> direct-await). Routing all three
/// variants through the same method keeps the method id, server dispatch branch, and service
/// implementation identical, so any measured difference is attributable to the client bridge alone.
/// </summary>
[RpcContract]
public interface IClientBridgeRpc : IService
{
    [NonCancellable]
    ValueTask<int> UnaryAsync(int value);

    [NonCancellable]
    ValueTask<int> ClientStreamAsync(IAsyncEnumerable<int> values);

    [NonCancellable]
    ValueTask<int> LatencyAsync(int value);
}
