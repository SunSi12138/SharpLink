using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;

namespace SharpLink.CompilerFiveFamily;

// These bodies are compiled, never invoked. One handle retains the exact generated service
// and its same-channel finite Prepare entry; no legacy raw cast or alternate identity mode.
public sealed class FiveFamilyCaller
{
    private readonly RpcProxy<IFiveFamilyContract> _proxy;

    public FiveFamilyCaller(ISharpLinkClient client, RpcProxyOptions? options = null)
        => _proxy = client.CreateProxy<IFiveFamilyContract>(options);

    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => _proxy.PrepareAsync(timeout, cancellationToken);

    public ValueTask<int> UnaryAsync(int value, CancellationToken cancellationToken)
        => _proxy.Service.UnaryAsync(value, cancellationToken);

    public ValueTask OneWayAsync(int value, CancellationToken cancellationToken)
        => _proxy.Service.OneWayAsync(value, cancellationToken);

    public ValueTask<int> ClientStreamingAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
        => _proxy.Service.ClientStreamingAsync(values, cancellationToken);

    public IAsyncEnumerable<int> ServerStreamingAsync(int value, CancellationToken cancellationToken)
        => _proxy.Service.ServerStreamingAsync(value, cancellationToken);

    public IAsyncEnumerable<int> DuplexStreamingAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
        => _proxy.Service.DuplexStreamingAsync(values, cancellationToken);

    public ValueTask<int> ClientStreamingWithSeedAsync(int seed, IAsyncEnumerable<int> values, CancellationToken cancellationToken)
        => _proxy.Service.ClientStreamingWithSeedAsync(seed, values, cancellationToken);

    public IAsyncEnumerable<int> DuplexStreamingWithSeedAsync(int seed, IAsyncEnumerable<int> values, CancellationToken cancellationToken)
        => _proxy.Service.DuplexStreamingWithSeedAsync(seed, values, cancellationToken);

    public static IRpcContractChannel CreateManualChannel(ISharpLinkClient client, RpcProxyOptions? options = null)
        => client.CreateContractChannel(client.GetLocalContract<IFiveFamilyContract>(), options);
}
