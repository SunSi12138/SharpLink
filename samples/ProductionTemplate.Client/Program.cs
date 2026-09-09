using System.Diagnostics;
using System.Net.Security;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using ProductionTemplate.Contracts;
using SharpLink.Client;
using SharpLink.Sdk;

[assembly: SharpLinkRpcContracts(typeof(IGreetingService))]

const int port = 50052;
const int maxPendingRequestsPerConnection = 1_024;
var serverIp = Environment.GetEnvironmentVariable("SHARPLINK_SERVER_IP") ?? "127.0.0.1";
var targetHost = Environment.GetEnvironmentVariable("SHARPLINK_TLS_TARGET_HOST") ?? "localhost";

using var loggerFactory = LoggerFactory.Create(logging =>
    logging.SetMinimumLevel(LogLevel.Information).AddSimpleConsole(options => options.SingleLine = true));
var telemetryLogger = loggerFactory.CreateLogger("SharpLink.Telemetry");
using var activityListener = new ActivityListener
{
    ShouldListenTo = static source => source.Name is "SharpLink.Client" or "SharpLink.Server",
    Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = activity => telemetryLogger.LogInformation(
        "rpc.trace source={Source} operation={Operation} duration_ms={DurationMs:F2}",
        activity.Source.Name,
        activity.OperationName,
        activity.Duration.TotalMilliseconds)
};
ActivitySource.AddActivityListener(activityListener);

var tlsOptions = new SslClientAuthenticationOptions
{
    TargetHost = targetHost,
    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
};
using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await using var client = SharpClientBuilder.Create()
    .UseLoggerFactory(loggerFactory)
    .UseRequestTimeout(TimeSpan.FromSeconds(5))
    .UseProtocol(options => options.MaxPendingRequestsPerConnection = maxPendingRequestsPerConnection)
    .UseTcp(
        serverIp,
        port,
        tlsOptions,
        tlsHandshakeTimeout: TimeSpan.FromSeconds(5))
    .Build();

await client.ConnectAsync(startupTimeout.Token);
await client.WaitForReadinessAsync(1, startupTimeout.Token);

var greeting = client.Get<IGreetingService>();
var reply = await greeting.GreetAsync(
    new GreetingRequest { Name = "production" },
    CancellationToken.None);
Console.WriteLine(reply.Message);

await client.StopAsync();
