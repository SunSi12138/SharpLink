using SharpLink.Server;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class ServerDecodeFairSchedulerCapacityTests
{
    [Test]
    public async Task DistinctConnectionQueuesShouldShareOneGlobalProductionPendingBound()
    {
        const int queueCapacity = 4;
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity);
        var runningKey = new object();
        var runningStarted = NewSignal();
        var releaseRunning = NewSignal();

        Ensure(executor.TryReserveQueueSlot(out var runningPermit) && runningPermit is not null,
            "running production work must reserve scheduler capacity");
        var running = executor.EnqueueReservedAsync(
            runningKey,
            runningPermit!,
            new ServerDecodeWorkItem(async _ =>
            {
                runningStarted.TrySetResult();
                await releaseRunning.Task.ConfigureAwait(false);
            }),
            CancellationToken.None).AsTask();
        await runningStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => executor.QueueReservations == 0 && executor.QueueDepth == 0,
            "worker start released the running request queue reservation");

        var queued = new List<Task>(queueCapacity);
        for (var index = 0; index < queueCapacity; index++)
        {
            Ensure(executor.TryReserveQueueSlot(out var permit) && permit is not null,
                $"connection {index} must share the available global pending capacity");
            queued.Add(executor.EnqueueReservedAsync(
                new object(),
                permit!,
                new ServerDecodeWorkItem(_ => ValueTask.CompletedTask),
                CancellationToken.None).AsTask());
        }

        await WaitUntilAsync(
            () => executor.QueueReservations == queueCapacity &&
                  executor.QueueDepth == queueCapacity &&
                  executor.ScheduledConnectionCount == queueCapacity,
            "all distinct connection queues consumed the one global pending budget");

        Ensure(!executor.TryReserveQueueSlot(out var rejectedPermit) && rejectedPermit is null,
            "an extra connection must not receive a private queue budget beyond the global capacity");
        Ensure(executor.QueueReservations == queueCapacity,
            "rejected scheduler admission must not perturb accepted queue reservations");

        releaseRunning.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(2));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(executor.QueueReservations == 0, "drain must release all global queue reservations");
        Ensure(executor.QueueDepth == 0, "drain must clear all pending work");
        Ensure(executor.ScheduledConnectionCount == 0,
            "drain must reclaim all per-connection scheduling metadata");
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Exception($"assert failed: {scenario}");
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
