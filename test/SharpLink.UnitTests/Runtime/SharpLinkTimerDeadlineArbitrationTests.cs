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
    public async Task TaskWaitShouldLetExpiredDeadlineWinLaterSuccess()
    {
        var provider = new ManualTimeProvider();
        var owner = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        var wait = SharpLinkTimer.WaitAsync(
            owner.Task, deadline, provider, CancellationToken.None).AsTask();

        provider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        owner.SetResult();

        Ensure(!await wait,
            "a source task that succeeds after the deadline must not replace deadline expiry");
    }

    [Test]
    public async Task TaskWaitShouldLetExpiredDeadlineWinLaterFault()
    {
        var provider = new ManualTimeProvider();
        var owner = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        var wait = SharpLinkTimer.WaitAsync(
            owner.Task, deadline, provider, CancellationToken.None).AsTask();

        provider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        owner.SetException(new InvalidOperationException("late fault"));

        Ensure(!await wait,
            "a source task that faults after the deadline must not replace deadline expiry");
    }

    [Test]
    public async Task TaskWaitShouldLetExpiredDeadlineWinLaterSourceCancellation()
    {
        var provider = new ManualTimeProvider();
        var owner = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        var wait = SharpLinkTimer.WaitAsync(
            owner.Task, deadline, provider, CancellationToken.None).AsTask();

        provider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        owner.SetCanceled();

        Ensure(!await wait,
            "a source task canceled after the deadline must not replace deadline expiry");
    }

    [Test]
    public async Task TaskWaitShouldPreserveSourceFaultBeforeDeadline()
    {
        var provider = new ManualTimeProvider();
        var owner = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("source won");
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        var wait = SharpLinkTimer.WaitAsync(
            owner.Task, deadline, provider, CancellationToken.None).AsTask();

        owner.SetException(expected);

        try
        {
            _ = await wait;
            throw new Exception("expected source fault");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(ReferenceEquals(exception, expected),
                "a source fault before the deadline must remain the terminal outcome");
        }
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
