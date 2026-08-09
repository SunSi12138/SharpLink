using System;
using System.Net;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using SharpLink.Abstractions;
using SharpLink.Client;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser(displayGenColumns: false)]
[AllStatisticsColumn]
public class EndpointIndexSelectionBenchmarks
{
    private ulong _excluded;
    private int _availableCount;
    private int _target;
    private int _cursor;

    [Params(1, 4, 16, 64)]
    public int EndpointCount { get; set; }

    [Params(0, 25, 75)]
    public int ExcludedPercent { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var excludedCount = EndpointCount * ExcludedPercent / 100;
        _excluded = excludedCount == 64
            ? ulong.MaxValue
            : (1UL << excludedCount) - 1;
        _availableCount = EndpointCount - excludedCount;
        _target = _availableCount / 2;
        _cursor = -1;
    }

    [Benchmark]
    public int RandomIndex()
        => StaticEndpointSelection.SelectRandomIndex(
            EndpointCount,
            _excluded,
            _availableCount,
            _target);

    [Benchmark]
    public int RoundRobinIndex()
        => StaticEndpointSelection.SelectRoundRobinIndex(
            ref _cursor,
            EndpointCount,
            _excluded);
}

[MemoryDiagnoser(displayGenColumns: false)]
[ThreadingDiagnoser]
[AllStatisticsColumn]
public class EndpointSelectionRpcBenchmarks
{
    private static readonly TimeSpan SHeartbeatInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan SHeartbeatTimeout = TimeSpan.FromHours(2);
    private BenchmarkEnvironment _environment = null!;

    [Params(2, 4, 16, 64)]
    public int EndpointCount { get; set; }

    [ParamsAllValues]
    public SharpLinkLoadBalancingStrategy Strategy { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _environment = await BenchmarkEnvironment.CreateAsync(
            createClientBuilder: port => SharpClientBuilder.Create()
                .UseHeartbeat(SHeartbeatInterval, SHeartbeatTimeout)
                .UseEndpoints(CreateEndpoints(port), SharpLinkTransportFactories.Sockets())
                .UseLoadBalancing(Strategy)
                .UseCluster(options =>
                {
                    options.MinReadyEndpoints = EndpointCount;
                    options.MaxConnections = EndpointCount;
                    options.MaxConnectionsPerEndpoint = 1;
                    options.MaxRetiringConnections = EndpointCount;
                }),
            expectedReadyConnections: EndpointCount).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async ValueTask Cleanup()
        => await _environment.DisposeAsync().ConfigureAwait(false);

    [Benchmark]
    public ValueTask<int> SelectAndInvoke()
        => _environment.Rpc.AddAsync(10, 20);

    private SharpLinkEndpoint[] CreateEndpoints(int port)
    {
        var endpoints = new SharpLinkEndpoint[EndpointCount];
        for (var index = 0; index < endpoints.Length; index++)
        {
            endpoints[index] = new SharpLinkEndpoint
            {
                Id = $"selection-{index}",
                Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
            };
        }
        return endpoints;
    }
}
