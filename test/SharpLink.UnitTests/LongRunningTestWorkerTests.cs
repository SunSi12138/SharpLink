namespace SharpLink.UnitTests;

public class LongRunningTestWorkerTests
{
    [Test]
    public async Task CleanupJoinShouldObserveAnAlreadyTerminatedWorkersTimeout()
    {
        var failure = new TimeoutException("The worker's semantic phase timed out.");
        var worker = Task.FromException(failure);
        await LongRunningTestWorker.JoinAsync(worker, TimeSpan.FromSeconds(10));
        if (LongRunningTestWorker.WaitForPhase(Task.FromResult(42), TimeSpan.FromSeconds(10)) != 42)
            throw new InvalidOperationException("A synchronous phase observation changed the worker result.");
        try
        {
            LongRunningTestWorker.WaitForPhase(worker, TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException observed) when (ReferenceEquals(observed, failure))
        {
            return;
        }
        throw new InvalidOperationException("A semantic phase must preserve its original failure identity.");
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
