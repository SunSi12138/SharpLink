using SharpLink.Server;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class ServerDecodeExecutorTests
{
    [Test]
    public async Task QueuedCancellationShouldCompleteCallerBeforeWorkerAndSkipProvider()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var secondExecutions = 0;

        var first = executor.EnqueueAsync(
            new ServerDecodeWorkItem(async _ =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.ConfigureAwait(false);
            }),
            CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cancellation = new CancellationTokenSource();
        var second = executor.EnqueueAsync(
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
        Ensure(executor.QueueDepth == 1,
            "published cancelled work remains queued until a worker observes and skips it");

        releaseFirst.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(executor.QueueDepth == 0, "drained executor queue depth");
        Ensure(executor.SkippedBeforeStart == 1, "cancelled queued work must be counted as skipped");
        Ensure(secondExecutions == 0, "skipped work must never execute provider code later");
    }

    [Test]
    public async Task BlockedWriterCancellationShouldRollbackPendingDepthWithoutPublication()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var thirdExecutions = 0;

        var first = executor.EnqueueAsync(
            new ServerDecodeWorkItem(async _ =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.ConfigureAwait(false);
            }),
            CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = executor.EnqueueAsync(
            new ServerDecodeWorkItem(_ => ValueTask.CompletedTask),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() => executor.QueueDepth == 1, "second decode was not queued");

        using var cancellation = new CancellationTokenSource();
        var third = executor.EnqueueAsync(
            new ServerDecodeWorkItem(_ =>
            {
                Interlocked.Increment(ref thirdExecutions);
                return ValueTask.CompletedTask;
            }),
            cancellation.Token).AsTask();
        await WaitUntilAsync(
            () => executor.QueueDepth == 2 && !third.IsCompleted,
            "third decode did not block behind the full bounded queue");

        cancellation.Cancel();
        await EnsureCancelledAsync(third, "blocked writer cancellation");
        Ensure(executor.QueueDepth == 1,
            "blocked writer cancellation must roll back its pending-depth ownership");
        Ensure(executor.SkippedBeforeStart == 0,
            "work cancelled before publication must never reach the worker skip path");
        Ensure(thirdExecutions == 0, "unpublished work must not execute provider code");

        releaseFirst.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(executor.QueueDepth == 0, "executor must drain after blocked-writer cancellation");
    }

    [Test]
    public async Task WorkerWinningCancellationRaceShouldKeepCallerJoinedUntilProviderReturns()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        var providerStarted = NewSignal();
        var releaseProvider = NewSignal();
        using var cancellation = new CancellationTokenSource();

        var operation = executor.EnqueueAsync(
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
    public async Task CompleteShouldStopPublicationAndDrainAlreadyPublishedWork()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var secondExecutions = 0;

        var first = executor.EnqueueAsync(
            new ServerDecodeWorkItem(async _ =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.ConfigureAwait(false);
            }),
            CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = executor.EnqueueAsync(
            new ServerDecodeWorkItem(_ =>
            {
                Interlocked.Increment(ref secondExecutions);
                return ValueTask.CompletedTask;
            }),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() => executor.QueueDepth == 1, "second decode was not queued");

        var completion = executor.CompleteAsync().AsTask();
        Ensure(!completion.IsCompleted, "completion must wait for running and queued work to drain");

        var rejected = executor.EnqueueAsync(
            new ServerDecodeWorkItem(_ => ValueTask.CompletedTask),
            CancellationToken.None).AsTask();
        await EnsureFailsAsync<InvalidOperationException>(rejected, "post-completion enqueue");

        releaseFirst.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(secondExecutions == 1, "work published before completion must drain exactly once");
        Ensure(executor.QueueDepth == 0, "completed executor queue depth");
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
