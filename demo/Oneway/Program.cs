using DemoBase;
using SharpLink.Runtime;
using SharpLink.Sdk;

const int port = 19093;

using var cts = new CancellationTokenSource();

var server = DemoTcp.CreateServer<IOnewayService, OnewayService>(port);
var serverTask = DemoTcp.StartServerAsync(server, cts.Token);
var client = DemoTcp.CreateClient(port);

try
{
    var proxy = client.CreateProxy<IOnewayService>();
    await client.ConnectAsync(cts.Token);
    await proxy.PrepareAsync(TimeSpan.FromSeconds(5), cts.Token);
    var service = proxy.Service;
    await service.FireAndForget("hello from oneway demo");
    Console.WriteLine("[Client] Oneway method sent.");

    await Task.Delay(150);
}
finally
{
    await DemoTcp.ShutdownAsync(cts, serverTask, client, server);
}

[RpcService]
public class OnewayService : IOnewayService
{
    public ValueTask FireAndForget(string message)
    {
        Console.WriteLine($"[Server] Oneway received: {message}");
        return ValueTask.CompletedTask;
    }
}

[RpcContract]
public interface IOnewayService : IService
{
    [Oneway]
    [NonCancellable]
    ValueTask FireAndForget(string message);
}
