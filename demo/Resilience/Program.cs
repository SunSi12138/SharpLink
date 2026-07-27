using System.Net;
using DemoBase;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

var portA = DemoStream.GetFreePort();
var portB = DemoStream.GetFreePort();
using var app = new CancellationTokenSource(TimeSpan.FromSeconds(20));

var serverA = CreateServer(portA, "node-a");
var serverB = CreateServer(portB, "node-b");
var serverTaskA = DemoTcp.StartServerAsync(serverA, app.Token);
var serverTaskB = DemoTcp.StartServerAsync(serverB, app.Token);

var client = SharpClientBuilder.Create()
    .UseEndpoints(
        [
            new SharpLinkEndpoint { Id = "node-a", Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), portA) },
            new SharpLinkEndpoint { Id = "node-b", Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), portB) }
        ],
        SharpLinkTransportFactories.Sockets())
    .UseCluster(options =>
    {
        options.MinReadyEndpoints = 2;
        options.MaxConnections = 2;
        options.MaxConnectionsPerEndpoint = 1;
    })
    .UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin)
    .UseRetry(options =>
    {
        options.MaxAttempts = 3;
        options.InitialBackoff = TimeSpan.FromMilliseconds(10);
        options.MaxBackoff = TimeSpan.FromMilliseconds(50);
        options.JitterRatio = 0;
    })
    .UseCircuitBreaker(options =>
    {
        options.MinimumThroughput = 4;
        options.FailureRatio = 0.5;
        options.SamplingDuration = TimeSpan.FromSeconds(10);
        options.BreakDuration = TimeSpan.FromSeconds(1);
    })
    .Build();

try
{
    await client.ConnectAsync(app.Token);
    var service = client.Get<IResilienceService>();
    var nodes = new HashSet<string>(StringComparer.Ordinal);
    for (var request = 0; request < 12; request++)
        nodes.Add(await service.GetNodeAsync(request, app.Token));

    Console.WriteLine($"ready endpoints: {string.Join(", ", nodes.Order())}");
    if (!nodes.SetEquals(["node-a", "node-b"]))
        throw new InvalidOperationException("The static cluster did not route to both ready endpoints.");
}
finally
{
    app.Cancel();
    await client.DisposeAsync();
    await serverA.DisposeAsync();
    await serverB.DisposeAsync();
    await Task.WhenAll(
        Task.WhenAny(serverTaskA, Task.Delay(300)),
        Task.WhenAny(serverTaskB, Task.Delay(300)));
}

static ISharpLinkServer CreateServer(int port, string node)
    => SharpLinkServerBuilder.Create()
        .UseTcp(port, IPAddress.Loopback.ToString())
        .ReplaceService<IResilienceService>(new ResilienceService(node))
        .Build();

[RpcContract]
public interface IResilienceService : IService
{
    [Idempotent]
    ValueTask<string> GetNodeAsync(int request, CancellationToken cancellationToken);
}

[RpcService]
public sealed class ResilienceService(string node) : IResilienceService
{
    public ValueTask<string> GetNodeAsync(int request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(node);
    }
}
