using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpLink.Client;
using SharpLink.Hosting;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

const int port = 19191;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSharpLinkServer(server =>
{
    server
        .UseTcp(port, "127.0.0.1")
        ;
});

builder.Services.AddSharpLinkClient(client =>
{
    client
        .UseTcp("127.0.0.1", port)
        ;
});

builder.Services.AddHostedService<HostRpcDemoService>();

await builder.Build().RunAsync();

public sealed class HostRpcDemoService(
    ISharpLinkClientAccessor clientAccessor,
    IHostApplicationLifetime appLifetime,
    ILogger<HostRpcDemoService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var clientGetter = clientAccessor.GetClientAsync(cancellationToken);
        var client = clientGetter.IsCompleted ? clientGetter.Result : await clientGetter;


        logger.LogInformation("Host RPC demo starting.");
        var hello = client.Get<IHelloService>();
        var result = await hello.Echo("HostApplication");
        logger.LogInformation("RPC result: {Result}", result);

        appLifetime.StopApplication();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[RpcContract]
public interface IHelloService : IService
{
    [NonCancellable]
    ValueTask<string> Echo(string name);
}

[RpcService]
public class HelloService : IHelloService
{
    public ValueTask<string> Echo(string name) => ValueTask.FromResult($"Hello {name}");
}
