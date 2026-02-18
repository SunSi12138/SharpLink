using DemoBase;
using SharpLink.Runtime;
using SharpLink.Sdk;

const int port = 19293;
using var appCts = new CancellationTokenSource();

RpcCodecRegistry.Initialize(MemoryPackCodec.Resolver);
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
    catch (TimeoutException)
    {
        Console.WriteLine("Default timeout triggered as expected.");
    }

    try
    {
        Console.WriteLine("2) Invoke [Timeout] method...");
        _ = await service.WorkWithMethodTimeout();
        Console.WriteLine("Unexpected success ([Timeout]).");
    }
    catch (TimeoutException)
    {
        Console.WriteLine("[Timeout] attribute triggered as expected.");
    }

    try
    {
        Console.WriteLine("3) Invoke no-[Timeout] method (should ignore default timeout)...");
        var noTimeoutResult = await service.WorkWithoutTimeoutAttribute();
        Console.WriteLine($"No-[Timeout] method completed: {noTimeoutResult}");
    }
    catch (TimeoutException)
    {
        Console.WriteLine("Unexpected timeout for no-[Timeout] method.");
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
    await DemoTcp.ShutdownAsync(appCts, serverTask, client as IDisposable, server as IDisposable);
}

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
