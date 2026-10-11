namespace SharpLink.UnitTests.Runtime;

public class ReadOwnershipDispatchProgressWatchdogTests
{
    [Test]
    public async Task AdvancingOperationsShouldOutliveMultipleWatchdogWindows()
    {
        var completion = new TaskCompletionSource();
        var progress = 0;
        var windows = 0;
        await ReadOwnershipPipeReaderDispatchTests.WaitForQueuedProgressAsync(
            completion.Task, () => progress, task =>
            {
                if (++windows < 3)
                {
                    progress += 1_000;
                    return Task.FromException(new TimeoutException("watchdog window"));
                }
                completion.SetResult();
                return task;
            });
        Ensure(windows == 3 && progress == 2_000,
            "aggregate elapsed windows must not reject a worker that keeps making progress");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NoProgressMustFailEvenAfterAnEarlierWindowAdvanced(bool advanceFirst)
    {
        var completion = new TaskCompletionSource();
        var progress = 0;
        var windows = 0;
        var error = await ObserveFailure(ReadOwnershipPipeReaderDispatchTests.WaitForQueuedProgressAsync(
            completion.Task, () => progress, _ =>
            {
                if (++windows == 1 && advanceFirst) progress = 1;
                return Task.FromException(new TimeoutException("watchdog window"));
            }));
        Ensure(error is TimeoutException && error.Message.Contains("no progress", StringComparison.Ordinal),
            "a stalled worker must fail instead of resetting its deadline indefinitely");
        Ensure(windows == (advanceFirst ? 2 : 1), "only windows with new progress may continue");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompletedWorkerFailureMustNeverBeMistakenForAnOuterTimeout(bool timeout)
    {
        Exception expected = timeout ? new TimeoutException("lost wakeup") : new InvalidOperationException("consumer failed");
        var completion = Task.FromException(expected);
        var error = await ObserveFailure(ReadOwnershipPipeReaderDispatchTests.WaitForQueuedProgressAsync(
            completion, () => 10_999, task => task));
        Ensure(ReferenceEquals(error, expected), "the original worker failure must remain observable");
    }

    [Test]
    public async Task CompletionRacingTheWatchdogMustRemainSuccessful()
    {
        var completion = new TaskCompletionSource();
        await ReadOwnershipPipeReaderDispatchTests.WaitForQueuedProgressAsync(
            completion.Task, () => 11_000, _ =>
            {
                completion.SetResult();
                return Task.FromException(new TimeoutException("racing watchdog window"));
            });
        Ensure(completion.Task.IsCompletedSuccessfully, "a completed worker must win over the outer timer");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WorkerCompletingDuringProgressSamplingMustKeepItsOutcome(bool fail)
    {
        var completion = new TaskCompletionSource();
        var failure = new InvalidOperationException("consumer failure raced progress sampling");
        var reads = 0;
        var error = await ObserveFailure(ReadOwnershipPipeReaderDispatchTests.WaitForQueuedProgressAsync(
            completion.Task, () =>
            {
                if (++reads == 2)
                {
                    if (fail) completion.SetException(failure);
                    else completion.SetResult();
                }
                return 0;
            }, _ => Task.FromException(new TimeoutException("watchdog window"))));
        Ensure(fail ? ReferenceEquals(error, failure) : error is null,
            "sampling progress must not replace a worker's concurrent terminal outcome");
    }

    private static async Task<Exception?> ObserveFailure(Task task)
    {
        try { await task; }
        catch (Exception error) { return error; }
        return null;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
