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
