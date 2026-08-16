using System.Collections.Generic;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

/// <summary>
/// Contract dedicated to issue #159's client-proxy bridge comparison. Each logical call has both a
/// <c>ValueTask</c> shape (proxy passthrough, Variant C) and a <c>Task</c> shape (proxy
/// <c>.AsTask()</c>, Variant A); the benchmark wraps the <c>ValueTask</c> shape in an
/// <c>async Task</c> direct-await to obtain Variant B. This keeps the generated proxy untouched
/// and measures the three bridge shapes end-to-end.
/// </summary>
[RpcContract]
public interface IClientBridgeRpc : IService
{
    [NonCancellable]
    ValueTask<int> UnaryValueTaskAsync(int value);

    [NonCancellable]
    Task<int> UnaryTaskAsync(int value);

    [NonCancellable]
    ValueTask UnaryNoResultValueTaskAsync(int value);

    [NonCancellable]
    Task UnaryNoResultTaskAsync(int value);

    [NonCancellable]
    ValueTask<int> ClientStreamValueTaskAsync(IAsyncEnumerable<int> values);

    [NonCancellable]
    Task<int> ClientStreamTaskAsync(IAsyncEnumerable<int> values);

    [NonCancellable]
    ValueTask<int> LatencyValueTaskAsync(int value);

    [NonCancellable]
    Task<int> LatencyTaskAsync(int value);
}
