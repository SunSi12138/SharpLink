using System.Diagnostics;
using DemoBase;
using SharpLink.Abstractions;
using SharpLink.Sdk;

var activityNames = new List<string>();
using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name.StartsWith("SharpLink", StringComparison.Ordinal),
    Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activity => activityNames.Add($"{activity.Source.Name}:{activity.DisplayName}")
};
ActivitySource.AddActivityListener(listener);

var clientInterceptor = new ConsoleClientInterceptor();
var serverInterceptor = new ConsoleServerInterceptor();
var port = DemoStream.GetFreePort();
using var app = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var server = DemoTcp.CreateServer<IObservedService, ObservedService>(port,
    builder => builder.AddInterceptor(serverInterceptor));
var serverTask = DemoTcp.StartServerAsync(server, app.Token);
var client = DemoTcp.CreateClient(port, builder => builder.AddInterceptor(clientInterceptor));

try
{
    await DemoTcp.EnsureConnectedAsync(client, app.Token);
    var value = await client.Get<IObservedService>().AddAsync(20, 22, app.Token);
    Console.WriteLine($"result={value}, client={clientInterceptor.LastStatus}, server={serverInterceptor.LastStatus}");
    Console.WriteLine(string.Join(Environment.NewLine, activityNames));
    if (value != 42 || activityNames.Count < 2)
        throw new InvalidOperationException("Interceptor or ActivitySource evidence was incomplete.");
}
finally
{
    await DemoTcp.ShutdownAsync(app, serverTask, client, server);
}

[RpcContract]
public interface IObservedService : IService
{
    ValueTask<int> AddAsync(int left, int right, CancellationToken cancellationToken);
}

[RpcService]
public sealed class ObservedService : IObservedService
{
    public ValueTask<int> AddAsync(int left, int right, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(left + right);
    }
}

public sealed class ConsoleClientInterceptor : ISharpLinkClientInterceptor
{
    public SharpLinkInvocationStatus LastStatus { get; private set; }

    public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
        SharpLinkClientInvocationContext context,
        SharpLinkClientInvocationDelegate next)
    {
        context.Options = context.Options with
        {
            Metadata = new SharpLinkMetadata(
                new KeyValuePair<string, string>("demo", "interceptor"))
        };
        var result = await next(context);
        LastStatus = context.Status;
        return result;
    }
}

public sealed class ConsoleServerInterceptor : ISharpLinkServerInterceptor
{
    public SharpLinkInvocationStatus LastStatus { get; private set; }

    public async ValueTask InvokeAsync(
        SharpLinkServerInvocationContext context,
        SharpLinkServerInvocationDelegate next)
    {
        await next(context);
        LastStatus = context.Status;
    }
}
