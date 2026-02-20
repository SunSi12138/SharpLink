using DemoBase;
using SharpLink.Sdk;

const int port = 19292;
using var appCts = new CancellationTokenSource();

var server = DemoTcp.CreateServer<ICancelService, CancelService>(port);
var serverTask = DemoTcp.StartServerAsync(server, appCts.Token);
var client = DemoTcp.CreateClient(port);

try
{
    await DemoTcp.EnsureConnectedAsync(client, appCts.Token, "Failed to connect cancel demo client.");

    var service = client.Get<ICancelService>();

    using var requestCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
    try
    {
        Console.WriteLine("Invoke SlowCountAsync(cancel after 600ms)...");
        var result = await service.SlowCountAsync(100, requestCts.Token);
        Console.WriteLine($"Unexpected success: {result}");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Canceled successfully via protocol cancel packet.");
    }
}
finally
{
    await DemoTcp.ShutdownAsync(appCts, serverTask, client as IDisposable, server as IDisposable);
}

[RpcContract]
public interface ICancelService : IService
{
    ValueTask<int> SlowCountAsync(int count, CancellationToken cancellationToken);
}

[RpcService]
public class CancelService : ICancelService
{
    public async ValueTask<int> SlowCountAsync(int count, CancellationToken cancellationToken)
    {
        var current = 0;
        while (current < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellationToken);
            current++;
        }

        return current;
    }
}
