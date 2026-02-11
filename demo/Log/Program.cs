using DemoBase;
using Microsoft.Extensions.Logging;
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
    builder.UseLogging(logging => logging
        .UseLoggerFactory(loggerFactory)
        .UseMinimumLogLevel(LogLevel.Information));
});
var serverTask = DemoTcp.StartServerAsync(server, appCts.Token);

var silentClient = DemoTcp.CreateClient(port);
var loggedClient = DemoTcp.CreateClient(port, builder =>
{
    builder.UseLogging(logging => logging
        .UseLoggerFactory(loggerFactory)
        .UseMinimumLogLevel(LogLevel.Information));
});

try
{
    Console.WriteLine("1) silent client: framework logs are disabled by default.");
    await DemoTcp.EnsureConnectedAsync(silentClient, appCts.Token, "Silent client failed to connect.");
    var silentService = silentClient.Get<ILogService>();
    var pongSilent = await silentService.PingAsync();
    Console.WriteLine($"silent call result: {pongSilent}");

    Console.WriteLine("2) logged client: framework logs enabled via UseLogging.");
    await DemoTcp.EnsureConnectedAsync(loggedClient, appCts.Token, "Logged client failed to connect.");
    var loggedService = loggedClient.Get<ILogService>();
    var pongLogged = await loggedService.PingAsync();
    Console.WriteLine($"logged call result: {pongLogged}");

    await Task.Delay(150, appCts.Token);
}
finally
{
    await DemoTcp.ShutdownAsync(appCts, serverTask, silentClient as IDisposable, loggedClient as IDisposable, server as IDisposable);
}

public interface ILogService : IService
{
    ValueTask<string> PingAsync();
}

[RpcService]
public class LogService : ILogService
{
    public ValueTask<string> PingAsync() => ValueTask.FromResult("PONG");
}
