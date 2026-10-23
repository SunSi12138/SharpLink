namespace SharpLink.UnitTests;

public class LongRunningTestWorkerTests
{
    [Test]
    public async Task CleanupJoinShouldObserveAnAlreadyTerminatedWorkersTimeout()
    {
        var worker = Task.FromException(new TimeoutException("The worker's semantic phase timed out."));
        await LongRunningTestWorker.JoinAsync(worker, TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task CleanupJoinMustRejectAnOwnerThatHasNotTerminated()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            try
            {
                await LongRunningTestWorker.JoinAsync(completion.Task, TimeSpan.FromMilliseconds(20));
            }
            catch (TimeoutException)
            {
                return;
            }
            throw new InvalidOperationException("Cleanup must not accept a worker that is still running.");
        }
        finally
        {
            completion.TrySetResult();
            await LongRunningTestWorker.JoinAsync(completion.Task, TimeSpan.FromSeconds(10));
        }
    }
}
