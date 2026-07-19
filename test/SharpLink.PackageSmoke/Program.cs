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
    }

    private static async Task RunTransportSmokeAsync(
        bool useSharedMemory,
        CancellationToken cancellationToken)
    {
        var sharedMemoryName = $"sharplink-package-smoke-{Guid.NewGuid():N}";
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IPackageSmokeService, PackageSmokeService>();
        if (useSharedMemory)
            serverBuilder.UseSharedMemory(sharedMemoryName);
        else
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());

        var localEndPoint = serverBuilder.Transport!.LocalEndPoint as IPEndPoint;
        if (!useSharedMemory && localEndPoint is null)
            throw new InvalidOperationException("Package smoke server did not expose its TCP endpoint.");
        var server = serverBuilder.Build();
        var serverTask = RunServerAsync(server, cancellationToken);

        var clientBuilder = SharpClientBuilder.Create();
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
                "native-codec",
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
}
