using System.Net;
using System.Net.Sockets;
using DemoBase;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

await RunTcpAsync();
await RunNamedPipeAsync();
if (Socket.OSSupportsUnixDomainSockets)
    await RunUdsAsync();
await RunSharedMemoryAsync();
await RunAnonymousPipeAsync();

static Task RunTcpAsync()
{
    var port = DemoStream.GetFreePort();
    return RunPairAsync(
        "tcp",
        SharpLinkServerBuilder.Create().UseTcp(port, IPAddress.Loopback.ToString()),
        SharpClientBuilder.Create().UseTcp(IPAddress.Loopback.ToString(), port));
}

static Task RunNamedPipeAsync()
{
    var name = $"sharplink-demo-{Guid.NewGuid():N}";
    return RunPairAsync(
        "named-pipe",
        SharpLinkServerBuilder.Create().UseNamedPipe(name),
        SharpClientBuilder.Create().UseNamedPipe(name));
}

static async Task RunUdsAsync()
{
    var path = Path.Combine(Path.GetTempPath(), $"sharplink-demo-{Guid.NewGuid():N}.sock");
    try
    {
        await RunPairAsync(
            "uds",
            SharpLinkServerBuilder.Create().UseUds(path),
            SharpClientBuilder.Create().UseUds(path));
    }
    finally
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

static Task RunSharedMemoryAsync()
{
    var name = $"sharplink-demo-{Guid.NewGuid():N}";
    return RunPairAsync(
        "shared-memory",
        SharpLinkServerBuilder.Create().UseSharedMemory(name),
        SharpClientBuilder.Create().UseSharedMemory(name));
}

static async Task RunAnonymousPipeAsync()
{
    using var app = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var serverBuilder = SharpLinkServerBuilder.Create().UseAnonymousPipe();
    var allocator = (IAnonymousPipeAllocator)serverBuilder.Transport!;
    using var offer = await allocator.AllocateAsync(app.Token);
    var server = serverBuilder.Build();
    var serverTask = DemoTcp.StartServerAsync(server, app.Token);
    var client = SharpClientBuilder.Create()
        .UseAnonymousPipe(offer.InHandle, offer.OutHandle)
        .Build();
    try
    {
        await client.ConnectAsync(app.Token);
        // This demo runs both peers in one process, so keep the offered handle copies alive
        // until the client is disposed. A parent launching a child process should instead call
        // CompleteHandleTransfer immediately after the child inherits both handles.
        await VerifyAsync("anonymous-pipe", client, app.Token);
    }
    finally
    {
        await DemoTcp.ShutdownAsync(app, serverTask, client, server);
    }
}

static async Task RunPairAsync(
    string name,
    SharpLinkServerBuilder serverBuilder,
    SharpClientBuilder clientBuilder)
{
    using var app = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var server = serverBuilder.Build();
    var serverTask = DemoTcp.StartServerAsync(server, app.Token);
    var client = clientBuilder.Build();
    try
    {
        await client.ConnectAsync(app.Token);
        await VerifyAsync(name, client, app.Token);
    }
    finally
    {
        await DemoTcp.ShutdownAsync(app, serverTask, client, server);
    }
}

static async Task VerifyAsync(string name, ISharpLinkClient client, CancellationToken cancellationToken)
{
    var response = await client.Get<ITransportService>().PingAsync(name, cancellationToken);
    Console.WriteLine(response);
    if (response != $"{name}:ok")
        throw new InvalidOperationException($"{name} transport returned an unexpected result.");
}

[RpcContract]
public interface ITransportService : IService
{
    ValueTask<string> PingAsync(string transport, CancellationToken cancellationToken);
}

[RpcService]
public sealed class TransportService : ITransportService
{
    public ValueTask<string> PingAsync(string transport, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult($"{transport}:ok");
    }
}
