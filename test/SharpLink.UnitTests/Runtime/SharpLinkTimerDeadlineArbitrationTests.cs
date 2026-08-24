using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class SharpLinkTimerDeadlineArbitrationTests
{
    [Test]
    public async Task TaskWaitShouldLetExpiredDeadlineWinLaterCallerCancellation()
    {
        var provider = new ManualTimeProvider();
        var owner = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        var wait = SharpLinkTimer.WaitAsync(
            owner.Task, deadline, provider, cancellation.Token).AsTask();

        provider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Ensure(!await wait,
            "an already-expired monotonic deadline must win over later caller cancellation");
    }

    [Test]
    public async Task TaskWaitShouldLetExpiredDeadlineWinLaterTaskCompletionWithoutTimerCallback()
    {
        var provider = new ManualTimeProvider();
        var owner = new TaskCompletionSource();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        var wait = SharpLinkTimer.WaitAsync(
            owner.Task, deadline, provider, CancellationToken.None).AsTask();

        provider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        owner.SetResult();

        Ensure(!await wait,
            "a task completing after the monotonic boundary must not beat a delayed deadline timer");
    }

    [Test]
    public async Task TaskWaitShouldPreserveTaskClaimedBeforeDeadlineWhenContinuationRunsLater()
    {
        var provider = new ManualTimeProvider();
        var owner = new TaskCompletionSource();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        var wait = SharpLinkTimer.WaitAsync(
            owner.Task, deadline, provider, CancellationToken.None).AsTask();

        provider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(4));
        owner.SetResult();
        provider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));

        Ensure(await wait,
            "a task that claims completion before the deadline must remain the winner later");
    }

    [Test]
    public async Task SemaphoreWaitShouldLetExpiredDeadlineWinLaterCallerCancellation()
    {
        var provider = new ManualTimeProvider();
        using var semaphore = new SemaphoreSlim(0, 1);
        using var cancellation = new CancellationTokenSource();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        var wait = SharpLinkTimer.WaitAsync(
            semaphore, deadline, provider, cancellation.Token).AsTask();

        provider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Ensure(!await wait,
            "an already-expired monotonic deadline must win over later semaphore-wait cancellation");
        Ensure(semaphore.CurrentCount == 0,
            "deadline arbitration must not leak a semaphore permit");
    }

    [Test]
    public async Task DeadlineAwareDelayShouldLetDeadlineWinAnExactBoundaryTie()
    {
        var provider = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        var delay = SharpLinkTimer.DelayAsync(
            TimeSpan.FromSeconds(5), deadline, provider, CancellationToken.None).AsTask();

        provider.Advance(TimeSpan.FromSeconds(5));

        Ensure(!await delay,
            "a blocking delay that reaches the exact deadline must not start another attempt");
    }

    [Test]
    public async Task TaskWaitShouldPreserveCallerCancellationBeforeDeadline()
    {
        var provider = new ManualTimeProvider();
        var owner = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        var wait = SharpLinkTimer.WaitAsync(
            owner.Task, deadline, provider, cancellation.Token).AsTask();

        cancellation.Cancel();

        try
        {
            _ = await wait;
            throw new Exception("expected caller cancellation");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
