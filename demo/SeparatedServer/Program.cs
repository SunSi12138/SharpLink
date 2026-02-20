using DemoBase;
using SeparatedContracts;
using SharpLink.Runtime;
using SharpLink.Sdk;

[assembly: SharpLinkRpcContracts(typeof(IGreetingService))]

const int port = 19110;

RpcCodecRegistry.Initialize(MemoryPackCodec.Resolver);

var cts = new CancellationTokenSource();
var server = DemoTcp.CreateServer<IGreetingService, GreetingService>(port);
var serverTask = DemoTcp.StartServerAsync(server, cts.Token);

ConsoleCancelEventHandler cancelHandler = (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
Console.CancelKeyPress += cancelHandler;

Console.WriteLine($"Separated server started at 127.0.0.1:{port}");
Console.WriteLine("Press Ctrl+C to stop.");

try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
}
catch (OperationCanceledException)
{
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
    await DemoTcp.ShutdownAsync(cts, serverTask, server as IDisposable);
}

[RpcService]
public class GreetingService : IGreetingService
{
    public ValueTask<string> Greet(GreetRequest request)
    {
        var repeat = Math.Max(1, request.Repeat);
        var text = string.Join(" | ", Enumerable.Range(1, repeat).Select(i => $"Hello {request.Name} #{i}"));
        return ValueTask.FromResult(text);
    }

    public ValueTask<int> Add(int left, int right) => ValueTask.FromResult(left + right);
}
