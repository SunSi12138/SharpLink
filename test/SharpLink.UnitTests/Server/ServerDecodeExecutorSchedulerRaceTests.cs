using SharpLink.Server;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class ServerDecodeExecutorSchedulerRaceTests
{
    [Test]
    public async Task CancellingLastReadyItemsShouldRetireWakePermitsWithAllWorkersBusy()
    {
        const int workerCount = 4;
        const int cancellationCycles = 128;

        await using var executor = new ServerDecodeExecutor(workerCount, queueCapacity: 8);
        var releaseWorkers = NewSignal();
        var startedSignals = new TaskCompletionSource[workerCount];
        var blockers = new Task[workerCount];

        for (var index = 0; index < workerCount; index++)
        {
            var started = NewSignal();
            startedSignals[index] = started;
            blockers[index] = executor.EnqueueAsync(
                new object(),
                new ServerDecodeWorkItem(async _ =>
                {
                    started.TrySetResult();
                    await releaseWorkers.Task.ConfigureAwait(false);
                }),
                CancellationToken.None).AsTask();
        }

        for (var index = 0; index < workerCount; index++)
            await startedSignals[index].Task.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(executor.QueueDepth == 0, "all worker blockers must be running rather than queued");
        Ensure(executor.ScheduledConnectionCount == 0,
            "running blockers must leave no ready connection metadata behind");
        Ensure(executor.ReadySignalCount == 0,
            "all blocker wake permits must already be owned by the busy workers");

        for (var cycle = 0; cycle < cancellationCycles; cycle++)
        {
            using var cancellation = new CancellationTokenSource();
            var cancelled = executor.EnqueueAsync(
                new object(),
                new ServerDecodeWorkItem(_ => ValueTask.CompletedTask),
                cancellation.Token).AsTask();

            await WaitUntilAsync(
                () => executor.QueueDepth == 1 && executor.ScheduledConnectionCount == 1,
                $"cycle {cycle} ready item publication");

            cancellation.Cancel();
            await EnsureCancelledAsync(cancelled, $"cycle {cycle} queued cancellation");
            Ensure(executor.QueueDepth == 0,
                $"cycle {cycle} cancellation must release queue depth");
            Ensure(executor.ScheduledConnectionCount == 0,
                $"cycle {cycle} cancellation must remove the last ready connection");
        }

        Ensure(executor.ReadySignalCount == 0,
            "repeated publish/cancel cycles must not accumulate historical ready permits");
        Ensure(executor.SkippedBeforeStart == cancellationCycles,
            "every cancelled ready item must be removed before provider start");

        releaseWorkers.TrySetResult();
        await Task.WhenAll(blockers).WaitAsync(TimeSpan.FromSeconds(5));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(executor.QueueDepth == 0, "multi-worker cancellation stress must drain queue depth");
        Ensure(executor.ScheduledConnectionCount == 0,
            "multi-worker cancellation stress must reclaim scheduler metadata");
    }

    [Test]
    public async Task DisposeShouldWaitForCompatibilityWriterThatAcquiredSlotBeforePublication()
    {
        var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        var slots = ReadPrivateField<SemaphoreSlim>(executor, "_compatibilitySlots");
        var schedulerGate = ReadPrivateField<Lock>(executor, "_schedulerGate");
        slots.Wait();

        Task enqueue;
        Task dispose;
        try
        {
            enqueue = executor.EnqueueAsync(
                new ServerDecodeWorkItem(_ => ValueTask.CompletedTask),
                CancellationToken.None).AsTask();
            Ensure(executor.QueueDepth == 1,
                "compatibility writer must own pending depth while blocked on queue capacity");

            lock (schedulerGate)
            {
                slots.Release();
                Ensure(
                    SpinWait.SpinUntil(() => slots.CurrentCount == 0, TimeSpan.FromSeconds(2)),
                    "compatibility writer did not acquire the released slot before publication");

                dispose = executor.DisposeAsync().AsTask();
                Ensure(!dispose.IsCompleted,
                    "dispose must remain joined to the admitted compatibility writer");
            }

            await EnsureFailsAsync<ServerDecodeExecutorClosedException>(
                enqueue,
                "compatibility writer crossing dispose before publication");
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));

            Ensure(executor.QueueDepth == 0,
                "compatibility writer rollback must release pending depth before disposal completes");
        }
        finally
        {
            if (Volatile.Read(ref dispose) is null)
                await executor.DisposeAsync();
        }
    }

    private static T ReadPrivateField<T>(ServerDecodeExecutor executor, string name)
    {
        var field = typeof(ServerDecodeExecutor).GetField(
            name,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find executor field {name}");
        return (T)field.GetValue(executor)!;
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

    private static async Task EnsureCancelledAsync(Task task, string scenario)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception($"assert failed: {scenario} should cancel");
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {scenario} did not complete");
        }
    }

    private static async Task EnsureFailsAsync<TException>(Task task, string scenario)
        where TException : Exception
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception($"assert failed: {scenario} should fail");
        }
        catch (TException)
        {
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {scenario} did not complete");
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
