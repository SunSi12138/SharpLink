using DemoBase;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;

const int port = 19293;
using var appCts = new CancellationTokenSource();

var server = DemoTcp.CreateServer<ITimeoutService, TimeoutService>(port);
var serverTask = DemoTcp.StartServerAsync(server, appCts.Token);
var client = DemoTcp.CreateClient(port, builder => builder.UseRequestTimeout(TimeSpan.FromMilliseconds(120)));

try
{
    await DemoTcp.EnsureConnectedAsync(client, appCts.Token, "Failed to connect timeout demo client.");

    var service = client.Get<ITimeoutService>();

    try
    {
        Console.WriteLine("1) Invoke default-timeout method...");
        _ = await service.WorkWithDefaultTimeout();
        Console.WriteLine("Unexpected success (default timeout).");
    }
    catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.DeadlineExceeded)
    {
        Console.WriteLine("Default timeout triggered as expected.");
    }

    try
    {
        Console.WriteLine("2) Invoke [Timeout] method...");
        _ = await service.WorkWithMethodTimeout();
        Console.WriteLine("Unexpected success ([Timeout]).");
    }
    catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.DeadlineExceeded)
    {
        Console.WriteLine("[Timeout] attribute triggered as expected.");
    }

    try
    {
        Console.WriteLine("3) Invoke no-[Timeout] unary method (uses client default timeout)...");
        _ = await service.WorkWithoutTimeoutAttribute();
        Console.WriteLine("Unexpected success (client default timeout).");
    }
    catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.DeadlineExceeded)
    {
        Console.WriteLine("Client default timeout applied as expected.");
    }

    try
    {
        var quick = await service.QuickSuccess();
        Console.WriteLine($"4) QuickSuccess: {quick}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Unexpected exception in QuickSuccess: {ex.GetType().Name}");
    }
}
finally
{
    await DemoTcp.ShutdownAsync(appCts, serverTask, client, server);
}

[RpcContract]
public interface ITimeoutService : IService
{
    [Timeout]
    ValueTask<int> WorkWithDefaultTimeout(CancellationToken cancellationToken = default);

    [Timeout(0.05)]
    ValueTask<int> WorkWithMethodTimeout(CancellationToken cancellationToken = default);

    ValueTask<int> WorkWithoutTimeoutAttribute();

    ValueTask<int> QuickSuccess();
}

[RpcService]
public class TimeoutService : ITimeoutService
{
    public async ValueTask<int> WorkWithDefaultTimeout(CancellationToken cancellationToken = default)
    {
        await Task.Delay(500, cancellationToken);
        return 1;
    }

    public async ValueTask<int> WorkWithMethodTimeout(CancellationToken cancellationToken = default)
    {
        await Task.Delay(500, cancellationToken);
        return 2;
    }

    public async ValueTask<int> WorkWithoutTimeoutAttribute()
    {
        await Task.Delay(500);
        return 3;
    }

    public ValueTask<int> QuickSuccess() => ValueTask.FromResult(42);
}
