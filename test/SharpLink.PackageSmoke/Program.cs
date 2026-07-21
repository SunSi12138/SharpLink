using System.Net;
using System.Net.Sockets;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.PackageSmoke;

[RpcContract]
public interface IPackageSmokeService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);

    [NonCancellable]
    ValueTask<PackageSmokeEnvelope> EchoAsync(PackageSmokeEnvelope value);
}

public sealed record PackageSmokeAddress(string City, int PostalCode);

public sealed record PackageSmokeEnvelope(
    string Name,
    PackageSmokeAddress Address,
    List<int> Values);

[RpcService]
public sealed class PackageSmokeService : IPackageSmokeService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public ValueTask<PackageSmokeEnvelope> EchoAsync(PackageSmokeEnvelope value) =>
        ValueTask.FromResult(value);
}

public static class Program
{
    public static async Task Main()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await RunTransportSmokeAsync(useSharedMemory: false, timeout.Token);
        await RunTransportSmokeAsync(useSharedMemory: true, timeout.Token);
        await RunStaticEndpointSmokeAsync(timeout.Token);
    }

    private static async Task RunTransportSmokeAsync(
        bool useSharedMemory,
        CancellationToken cancellationToken)
    {
        var sharedMemoryName = $"sharplink-package-smoke-{Guid.NewGuid():N}";
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseAdmissionControl(options => options.Global.UseConcurrency(64));
        if (useSharedMemory)
            serverBuilder.UseSharedMemory(sharedMemoryName);
        else
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());

        var localEndPoint = serverBuilder.Transport!.LocalEndPoint as IPEndPoint;
        if (!useSharedMemory && localEndPoint is null)
            throw new InvalidOperationException("Package smoke server did not expose its TCP endpoint.");
        var server = serverBuilder.Build();
        var serverTask = RunServerAsync(server, cancellationToken);

        var clientBuilder = SharpClientBuilder.Create()
            .UseRuntime(ConfigureCompression);
        if (useSharedMemory)
            clientBuilder.UseSharedMemory(sharedMemoryName);
        else
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(), localEndPoint!.Port);
        var client = clientBuilder.Build();

        try
        {
            await client.ConnectAsync(cancellationToken);

            var proxy = client.Get<IPackageSmokeService>();
            var result = await proxy.AddAsync(20, 22);
            if (result != 42)
                throw new InvalidOperationException($"Package smoke returned {result} instead of 42.");

            var expected = new PackageSmokeEnvelope(
                new string('p', 4096),
                new PackageSmokeAddress("Shanghai", 200000),
                [1, 2, 3]);
            var actual = await proxy.EchoAsync(expected);
            if (actual.Name != expected.Name ||
                actual.Address != expected.Address ||
                !actual.Values.SequenceEqual(expected.Values))
                throw new InvalidOperationException("Package smoke generated DTO codec round-trip failed.");
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
    }

    private static async Task RunStaticEndpointSmokeAsync(CancellationToken cancellationToken)
    {
        var firstBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseAdmissionControl(options => options.Global.UseConcurrency(64))
            .UseTcp(0, IPAddress.Loopback.ToString());
        var secondBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseAdmissionControl(options => options.Global.UseConcurrency(64))
            .UseTcp(0, IPAddress.Loopback.ToString());
        var firstPort = ((IPEndPoint)firstBuilder.Transport!.LocalEndPoint!).Port;
        var secondPort = ((IPEndPoint)secondBuilder.Transport!.LocalEndPoint!).Port;
        var firstServer = firstBuilder.Build();
        var secondServer = secondBuilder.Build();
        var firstTask = RunServerAsync(firstServer, cancellationToken);
        var secondTask = RunServerAsync(secondServer, cancellationToken);
        var endpoints = new[]
        {
            new SharpLinkEndpoint
            {
                Id = "first",
                Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), firstPort),
                Attributes = new Dictionary<string, string> { ["zone"] = "a" }
            },
            new SharpLinkEndpoint
            {
                Id = "second",
                Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), secondPort),
                Attributes = new Dictionary<string, string> { ["zone"] = "b" }
            }
        };
        var client = SharpClientBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseEndpoints(
                endpoints,
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin)
            .Build();

        try
        {
            await client.ConnectAsync(cancellationToken);
            if (await client.Get<IPackageSmokeService>().AddAsync(20, 22) != 42)
                throw new InvalidOperationException("Static endpoint package smoke returned an unexpected result.");

            await using var dynamicClient = SharpClientBuilder.Create()
                .UseRuntime(ConfigureCompression)
                .UseEndpointResolver(
                    new DelegateSharpLinkEndpointResolver(
                        _ => ValueTask.FromResult(new SharpLinkEndpointSnapshot(1, endpoints))),
                    SharpLinkTransportFactories.Sockets())
                .UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin)
                .Build();
            await dynamicClient.ConnectAsync(cancellationToken);
            if (await dynamicClient.Get<IPackageSmokeService>().AddAsync(20, 22) != 42)
                throw new InvalidOperationException("Dynamic endpoint package smoke returned an unexpected result.");
        }
        finally
        {
            await client.DisposeAsync();
            await firstServer.DisposeAsync();
            await secondServer.DisposeAsync();
            await Task.WhenAny(firstTask, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            await Task.WhenAny(secondTask, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
    }

    private static async Task RunServerAsync(ISharpLinkServer server, CancellationToken cancellationToken)
    {
        try
        {
            await server.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void ConfigureCompression(SharpLinkRuntimeOptions options)
        => options.Compression.Providers.Add(SharpLinkCompressionProviders.CreateBrotli());
}
