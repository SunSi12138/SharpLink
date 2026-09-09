using System.Net;
using QuickStart.Contracts;
using SharpLink.Server;
using SharpLink.Sdk;

[assembly: SharpLinkRpcContracts(typeof(IGreetingService))]

const int port = 50051;
var runOnce = args.Contains("--once", StringComparer.Ordinal);
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
        .UseTcp(port, IPAddress.Loopback)
        .Build();

    var runTask = server.RunAsync(runCancellation.Token).AsTask();
    Console.WriteLine($"QUICKSTART_SERVER_READY http=127.0.0.1:{port}");

    var requestedStop = runOnce ? QuickStartState.FirstCallCompleted.Task : stopRequested.Task;
    var completed = await Task.WhenAny(runTask, requestedStop);
    if (completed == runTask)
    {
        await runTask;
    }
    else
    {
        await server.StopAsync(TimeSpan.FromSeconds(5));
        await runCancellation.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
        }
    }

    Console.WriteLine("QUICKSTART_SERVER_STOPPED");
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
        var reply = new GreetingReply { Message = $"Hello, {request.Name}!" };
        QuickStartState.FirstCallCompleted.TrySetResult();
        return ValueTask.FromResult(reply);
    }
}

internal static class QuickStartState
{
    internal static TaskCompletionSource FirstCallCompleted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
