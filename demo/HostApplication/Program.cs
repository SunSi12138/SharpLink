using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharpLink.Client;
using SharpLink.Hosting;
using SharpLink.Sdk;
using SharpLink.Runtime;
using SharpLink.Server;

const int port = 19191;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSharpLinkServer(server =>
{
    server
        .AddService<IHelloService, HelloService>()
        .UseTcp(port, "127.0.0.1")
        .UseSerializer(new MemoryPackSerializerAdaptor());
});

builder.Services.AddSharpLinkClient(client =>
{
    client
        .UseTcp("127.0.0.1", port)
        .UseSerializer(new MemoryPackSerializerAdaptor());
});

builder.Services.AddHostedService<HostRpcDemoService>();

await builder.Build().RunAsync();

public sealed class HostRpcDemoService(ISharpLinkClientAccessor clientAccessor, IHostApplicationLifetime appLifetime) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var client = clientAccessor.Client
                     ?? throw new InvalidOperationException("SharpLink client is not ready.");

        var hello = client.Get<IHelloService>();
        var result = await hello.Echo("HostApplication");
        Console.WriteLine($"RPC Result: {result}");

        appLifetime.StopApplication();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public interface IHelloService : IService
{
    ValueTask<string> Echo(string name);
}

[RpcService]
public class HelloService : IHelloService
{
    public ValueTask<string> Echo(string name) => ValueTask.FromResult($"Hello {name}");
}
