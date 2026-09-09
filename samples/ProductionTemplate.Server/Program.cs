using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using ProductionTemplate.Contracts;
using SharpLink.Server;
using SharpLink.Sdk;

[assembly: SharpLinkRpcContracts(typeof(IGreetingService))]

const int port = 50052;
const int maxPendingRequestsPerConnection = 1_024;
const int maxConcurrentCalls = 256;
const int maxQueuedCalls = 512;
var certificatePath = Environment.GetEnvironmentVariable("SHARPLINK_TLS_CERT_PATH")
    ?? throw new InvalidOperationException(
        "Set SHARPLINK_TLS_CERT_PATH to a deployment-provided PKCS#12 certificate.");
var certificatePassword = Environment.GetEnvironmentVariable("SHARPLINK_TLS_CERT_PASSWORD");

using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
    certificatePath,
    certificatePassword);
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

var tlsOptions = new SslServerAuthenticationOptions
{
    ServerCertificate = certificate,
    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
};
var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
ConsoleCancelEventHandler cancelHandler = (_, e) =>
{
    e.Cancel = true;
    stopRequested.TrySetResult();
};
Console.CancelKeyPress += cancelHandler;

try
{
    using var runCancellation = new CancellationTokenSource();
    await using var server = SharpLinkServerBuilder.Create()
        .UseLoggerFactory(loggerFactory)
        .UseConnectionAdmission(options =>
        {
            options.MaxConcurrentConnections = 512;
            options.MaxConcurrentHandshakes = 32;
        })
        .UseAdmissionControl(options =>
        {
            options.Global.UseConcurrency(maxConcurrentCalls);
            options.MaxQueuedCalls = maxQueuedCalls;
            options.MaxQueuedBytes = 16 * 1024 * 1024;
            options.MaxQueueDelay = TimeSpan.FromSeconds(2);
        })
        .UseProtocol(options =>
        {
            options.MaxPendingRequestsPerConnection = maxPendingRequestsPerConnection;
            options.MaxConcurrentStreamsPerConnection = 128;
        })
        .UseTcp(
            port,
            tlsOptions,
            IPAddress.Loopback,
            tlsHandshakeTimeout: TimeSpan.FromSeconds(5))
        .Build();

    var runTask = server.RunAsync(runCancellation.Token).AsTask();
    Console.WriteLine($"PRODUCTION_TEMPLATE_SERVER_READY https=localhost:{port}");

    var completed = await Task.WhenAny(runTask, stopRequested.Task);
    if (completed == runTask)
    {
        await runTask;
    }
    else
    {
        await server.StopAsync(TimeSpan.FromSeconds(30));
        await runCancellation.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
        }
    }
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

[RpcService]
public sealed class GreetingService : IGreetingService
{
    public ValueTask<GreetingReply> GreetAsync(
        GreetingRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GreetingReply
        {
            Message = $"Hello securely, {request.Name}!"
        });
    }
}
