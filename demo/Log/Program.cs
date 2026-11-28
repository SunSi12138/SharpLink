using DemoBase;
using Microsoft.Extensions.Logging;
using SharpLink.Runtime;
using SharpLink.Sdk;

const int port = 19393;
var appCts = new CancellationTokenSource();

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Debug)
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
});

var server = DemoTcp.CreateServer<ILogService, LogService>(port, builder =>
{
    builder.UseLoggerFactory(loggerFactory);
});
var serverTask = DemoTcp.StartServerAsync(server, appCts.Token);

var silentClient = DemoTcp.CreateClient(port);
var loggedClient = DemoTcp.CreateClient(port, builder =>
{
    builder.UseLoggerFactory(loggerFactory);
});

try
{
    Console.WriteLine("1) silent client: framework logs are disabled by default.");
    var silent = silentClient.CreateProxy<ILogService>();
    await silentClient.ConnectAsync(appCts.Token);
    await silent.PrepareAsync(TimeSpan.FromSeconds(5), appCts.Token);
    var pongSilent = await silent.Service.PingAsync();
    Console.WriteLine($"silent call result: {pongSilent}");

    Console.WriteLine("2) logged client: framework logs enabled via UseLoggerFactory.");
    var logged = loggedClient.CreateProxy<ILogService>();
    await loggedClient.ConnectAsync(appCts.Token);
    await logged.PrepareAsync(TimeSpan.FromSeconds(5), appCts.Token);
    var pongLogged = await logged.Service.PingAsync();
    Console.WriteLine($"logged call result: {pongLogged}");

    await Task.Delay(150, appCts.Token);
}
finally
{
    await DemoTcp.ShutdownAsync(appCts, serverTask, silentClient, loggedClient, server);
}
[RpcContract]
public interface ILogService : IService
{
    [NonCancellable]
    ValueTask<string> PingAsync();
}

[RpcService]
public class LogService : ILogService
{
    public ValueTask<string> PingAsync() => ValueTask.FromResult("PONG");
}
