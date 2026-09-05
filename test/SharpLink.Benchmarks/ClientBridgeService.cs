using System.Collections.Generic;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

[RpcService]
public sealed class ClientBridgeRpcService : IClientBridgeRpc
{
    public ValueTask<int> UnaryAsync(int value) => ValueTask.FromResult(value + 1);

    public async ValueTask<int> ClientStreamAsync(IAsyncEnumerable<int> values)
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
    public async ValueTask<int> LatencyAsync(int value)
    {
        await Task.Delay(1).ConfigureAwait(false);
        return value + 1;
    }
}
