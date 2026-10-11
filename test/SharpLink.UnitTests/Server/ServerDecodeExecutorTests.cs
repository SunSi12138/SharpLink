using SharpLink.Server;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class ServerDecodeExecutorTests
{
    [Test]
    public async Task QueuedCancellationShouldCompleteCallerBeforeWorkerAndRemovePendingOwnership()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var secondExecutions = 0;

        var first = EnqueueProduction(executor,
            new ServerDecodeWorkItem(async _ =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.ConfigureAwait(false);
            }),
            CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cancellation = new CancellationTokenSource();
        var second = EnqueueProduction(executor,
            new ServerDecodeWorkItem(_ =>
            {
                Interlocked.Increment(ref secondExecutions);
                return ValueTask.CompletedTask;
            }),
            cancellation.Token).AsTask();
        await WaitUntilAsync(() => executor.QueueDepth == 1, "second decode was not queued");

        cancellation.Cancel();
        await EnsureCancelledAsync(second, "queued decode cancellation");
        Ensure(secondExecutions == 0, "cancelled queued work must not execute provider code");
        Ensure(executor.QueueDepth == 0,
            "cancelled queued work must release pending scheduler ownership immediately");
        Ensure(executor.ScheduledConnectionCount == 0,
            "empty connection scheduling metadata must be reclaimed after queued cancellation");
        Ensure(executor.SkippedBeforeStart == 1,
            "cancelled queued work must be counted as skipped before provider start");

        releaseFirst.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(executor.QueueDepth == 0, "drained executor queue depth");
        Ensure(secondExecutions == 0, "removed work must never execute provider code later");
    }

    [Test]
    public async Task ProductionQueueShouldRejectOverCapacityWithoutBlockingPublication()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        var started = NewSignal();
        var release = NewSignal();
        var first = EnqueueProduction(executor,
            new ServerDecodeWorkItem(async _ =>
            {
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }), CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = EnqueueProduction(executor,
            new ServerDecodeWorkItem(_ => ValueTask.CompletedTask),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() => executor.QueueReservations == 1
            && executor.QueueDepth == 1, "second work must own the one queue reservation");

        Ensure(!executor.TryReserveQueueSlot(out var rejected) && rejected is null,
            "a third queued production request must immediately fail global admission");
        Ensure(executor.QueueReservations == 1 && executor.QueueDepth == 1,
            "capacity rejection cannot modify the already accepted pending work");
        Ensure(executor.SkippedBeforeStart == 0,
            "a request never admitted to the scheduler cannot be counted as skipped");

        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(executor.QueueReservations == 0 && executor.QueueDepth == 0,
            "worker dequeue and completion must release all reserved queue ownership");
        Ensure(executor.ScheduledConnectionCount == 0,
            "the production fair scheduler must reclaim its empty metadata");
    }

    [Test]
    public async Task WorkerWinningCancellationRaceShouldKeepCallerJoinedUntilProviderReturns()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        var providerStarted = NewSignal();
        var releaseProvider = NewSignal();
        using var cancellation = new CancellationTokenSource();

        var operation = EnqueueProduction(executor,
            new ServerDecodeWorkItem(async _ =>
            {
                providerStarted.TrySetResult();
                await releaseProvider.Task.ConfigureAwait(false);
            }),
            cancellation.Token).AsTask();
        await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        await Task.Yield();
        Ensure(!operation.IsCompleted,
            "once the worker owns provider execution cancellation must not release the caller early");

        releaseProvider.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(2));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(executor.SkippedBeforeStart == 0, "running work must not be counted as queue-skipped");
    }

    [Test]
    public async Task FairSchedulingShouldServeSecondConnectionBeforeFirstConnectionGetsAnotherTurn()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 8);
        var connectionA = new object();
        var connectionB = new object();
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var order = new ConcurrentQueue<string>();

        var first = EnqueueProduction(executor,
            connectionA,
            new ServerDecodeWorkItem(async _ =>
            {
                order.Enqueue("A1");
                firstStarted.TrySetResult();
                await releaseFirst.Task.ConfigureAwait(false);
            }),
            CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var a2 = EnqueueProduction(executor,
            connectionA,
            NewRecordingWorkItem(order, "A2"),
            CancellationToken.None).AsTask();
        var a3 = EnqueueProduction(executor,
            connectionA,
            NewRecordingWorkItem(order, "A3"),
            CancellationToken.None).AsTask();
        var b1 = EnqueueProduction(executor,
            connectionB,
            NewRecordingWorkItem(order, "B1"),
            CancellationToken.None).AsTask();

        await WaitUntilAsync(
            () => executor.QueueDepth == 3 && executor.ScheduledConnectionCount == 2,
            "both connection queues were not scheduled");

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, a2, a3, b1).WaitAsync(TimeSpan.FromSeconds(2));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var observed = order.ToArray();
        Ensure(Array.IndexOf(observed, "B1") < Array.IndexOf(observed, "A3"),
            "connection B must receive service before connection A receives a second queued turn");
        Ensure(executor.ScheduledConnectionCount == 0, "completed connection queues must be reclaimed");
    }

    [Test]
    public async Task UnevenBacklogShouldNotStarveSecondConnection()
    {
        const int aBacklog = 64;
        const int bBacklog = 8;

        await using var executor = new ServerDecodeExecutor(
            workerCount: 1,
            queueCapacity: aBacklog + bBacklog + 1);
        var connectionA = new object();
        var connectionB = new object();
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var order = new ConcurrentQueue<string>();
        var operations = new List<Task>(aBacklog + bBacklog + 1);

        operations.Add(EnqueueProduction(executor,
            connectionA,
            new ServerDecodeWorkItem(async _ =>
            {
                order.Enqueue("A0");
                firstStarted.TrySetResult();
                await releaseFirst.Task.ConfigureAwait(false);
            }),
            CancellationToken.None).AsTask());
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var index = 1; index <= aBacklog; index++)
        {
            operations.Add(EnqueueProduction(executor,
                connectionA,
                NewRecordingWorkItem(order, $"A{index}"),
                CancellationToken.None).AsTask());
        }
        for (var index = 1; index <= bBacklog; index++)
        {
            operations.Add(EnqueueProduction(executor,
                connectionB,
                NewRecordingWorkItem(order, $"B{index}"),
                CancellationToken.None).AsTask());
        }

        await WaitUntilAsync(
            () => executor.QueueDepth == aBacklog + bBacklog,
            "uneven backlog was not fully queued");
        releaseFirst.TrySetResult();

        await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(5));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var observed = order.ToArray();
        for (var index = 1; index <= bBacklog; index++)
        {
            var position = Array.IndexOf(observed, $"B{index}");
            Ensure(position >= 0 && position <= index * 2,
                $"B{index} must receive a bounded round-robin turn under A's sustained backlog");
        }
        Ensure(executor.QueueDepth == 0, "stress drain must clear all pending work");
        Ensure(executor.ScheduledConnectionCount == 0, "stress drain must reclaim all connection metadata");
    }

    [Test]
    public async Task CancellingOneConnectionQueueShouldNotDelayAnotherConnection()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 4);
        var connectionA = new object();
        var connectionB = new object();
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var bStarted = NewSignal();
        var cancelledExecutions = 0;

        var first = EnqueueProduction(executor,
            connectionA,
            new ServerDecodeWorkItem(async _ =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.ConfigureAwait(false);
            }),
            CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cancellation = new CancellationTokenSource();
        var cancelled = EnqueueProduction(executor,
            connectionA,
            new ServerDecodeWorkItem(_ =>
            {
                Interlocked.Increment(ref cancelledExecutions);
                return ValueTask.CompletedTask;
            }),
            cancellation.Token).AsTask();
        var other = EnqueueProduction(executor,
            connectionB,
            new ServerDecodeWorkItem(_ =>
            {
                bStarted.TrySetResult();
                return ValueTask.CompletedTask;
            }),
            CancellationToken.None).AsTask();

        await WaitUntilAsync(() => executor.QueueDepth == 2, "two queued connections were not published");
        cancellation.Cancel();
        await EnsureCancelledAsync(cancelled, "connection A queued cancellation");
        await WaitUntilAsync(
            () => executor.QueueDepth == 1 && executor.ScheduledConnectionCount == 1,
            "cancelled connection queue ownership was not reclaimed");

        releaseFirst.TrySetResult();
        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(first, other).WaitAsync(TimeSpan.FromSeconds(2));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(cancelledExecutions == 0, "cancelled connection work must never execute provider code");
        Ensure(executor.QueueDepth == 0, "remaining connection must drain normally");
    }

    [Test]
    public async Task StopAcceptingShouldRejectNewReservationsAndDrainPublishedWork()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        var started = NewSignal();
        var release = NewSignal();
        var executedSecond = 0;

        var first = EnqueueProduction(executor,
            new ServerDecodeWorkItem(async _ =>
            {
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }), CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = EnqueueProduction(executor,
            new ServerDecodeWorkItem(_ =>
            {
                Interlocked.Increment(ref executedSecond);
                return ValueTask.CompletedTask;
            }), CancellationToken.None).AsTask();
        await WaitUntilAsync(() => executor.QueueReservations == 1
            && executor.QueueDepth == 1, "second request must be published before stop");

        executor.StopAccepting();
        Ensure(!executor.IsAccepting, "StopAccepting must seal publication synchronously");
        Ensure(!executor.TryReserveQueueSlot(out var rejectedPermit) && rejectedPermit is null,
            "post-stop request must not acquire queue admission");

        var rejected = EnqueueProduction(executor,
            new ServerDecodeWorkItem(_ => ValueTask.CompletedTask),
            CancellationToken.None).AsTask();
        await EnsureFailsAsync<ServerDecodeExecutorClosedException>(
            rejected, "post-stop production admission");
        Ensure(executor.QueueDepth == 1 && executor.QueueReservations == 1,
            "rejected requests cannot consume the already published work reservation");

        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(executedSecond == 1, "published work must drain and execute exactly once");
        Ensure(executor.QueueReservations == 0 && executor.QueueDepth == 0,
            "drained executor must release all production queue reservations");
        Ensure(executor.ScheduledConnectionCount == 0,
            "drain must release all fair scheduling metadata");
    }

    [Test]
    public async Task CompleteShouldStopPublicationAndDrainAlreadyPublishedWork()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var secondExecutions = 0;

        var first = EnqueueProduction(executor,
            new ServerDecodeWorkItem(async _ =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.ConfigureAwait(false);
            }),
            CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = EnqueueProduction(executor,
            new ServerDecodeWorkItem(_ =>
            {
                Interlocked.Increment(ref secondExecutions);
                return ValueTask.CompletedTask;
            }),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() => executor.QueueDepth == 1, "second decode was not queued");

        var completion = executor.CompleteAsync().AsTask();
        Ensure(!completion.IsCompleted, "completion must wait for running and queued work to drain");

        var rejected = EnqueueProduction(executor,
            new ServerDecodeWorkItem(_ => ValueTask.CompletedTask),
            CancellationToken.None).AsTask();
        await EnsureFailsAsync<InvalidOperationException>(rejected, "post-completion enqueue");

        releaseFirst.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(secondExecutions == 1, "work published before completion must drain exactly once");
        Ensure(executor.QueueDepth == 0, "completed executor queue depth");
        Ensure(executor.ScheduledConnectionCount == 0, "completion must reclaim scheduler metadata");
    }

    // Tests now publish through the exact, globally bounded production queue API.
    private static ValueTask EnqueueProduction(
        ServerDecodeExecutor executor,
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
        => EnqueueProduction(executor, executor, workItem, cancellationToken);

    private static ValueTask EnqueueProduction(
        ServerDecodeExecutor executor,
        object schedulingKey,
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (!executor.TryReserveQueueSlot(out var queuePermit) || queuePermit is null)
            return cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled(cancellationToken)
                : ValueTask.FromException(new ServerDecodeExecutorClosedException());

        return executor.EnqueueReservedAsync(
            schedulingKey, queuePermit, workItem, cancellationToken);
    }

    private static ServerDecodeWorkItem NewRecordingWorkItem(
        ConcurrentQueue<string> order,
        string value)
        => new(_ =>
        {
            order.Enqueue(value);
            return ValueTask.CompletedTask;
        });

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
