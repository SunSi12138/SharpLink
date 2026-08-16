using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

[RpcService]
public sealed class ClientBridgeRpcService : IClientBridgeRpc
{
    public ValueTask<int> UnaryValueTaskAsync(int value) => ValueTask.FromResult(value + 1);

    public Task<int> UnaryTaskAsync(int value) => Task.FromResult(value + 1);

    public ValueTask UnaryNoResultValueTaskAsync(int value) => ValueTask.CompletedTask;

    public Task UnaryNoResultTaskAsync(int value) => Task.CompletedTask;

    public async ValueTask<int> ClientStreamValueTaskAsync(IAsyncEnumerable<int> values)
    {
        var sum = 0;
        await foreach (var value in values.ConfigureAwait(false))
        {
            sum += value;
        }

        return sum;
    }

    public async Task<int> ClientStreamTaskAsync(IAsyncEnumerable<int> values)
    {
        var sum = 0;
        await foreach (var value in values.ConfigureAwait(false))
        {
            sum += value;
        }

        return sum;
    }

    // Injected latency: force a genuine suspension so the bridge cost is measured, not a
    // loopback that accidentally completes synchronously.
    public async ValueTask<int> LatencyValueTaskAsync(int value)
    {
        await Task.Delay(1).ConfigureAwait(false);
        return value + 1;
    }

    public async Task<int> LatencyTaskAsync(int value)
    {
        await Task.Delay(1).ConfigureAwait(false);
        return value + 1;
    }
}
