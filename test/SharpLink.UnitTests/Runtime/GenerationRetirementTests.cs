namespace SharpLink.UnitTests.Runtime;

public class GenerationRetirementTests
{
    [Test]
    public async Task CallerCancellationShouldStopWaitingWithoutCancellingFrameworkCleanup()
    {
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retirement = new SharpLinkRetirementHandle<int>(completion.Task);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = false;
        try
        {
            _ = await retirement.WaitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Ensure(cancelled, "caller cancellation must cancel only the retirement wait");
        Ensure(!completion.Task.IsCompleted,
            "caller cancellation must not cancel framework-owned retirement cleanup");

        completion.SetResult(42);
        Ensure(await retirement.WaitAsync() == 42,
            "framework-owned retirement must remain observable after the caller stops waiting");
    }

    [Test]
    public async Task BoundedWaitShouldTimeOutWithoutCancellingFrameworkCleanup()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retirement = new SharpLinkRetirementHandle(completion.Task);

        Ensure(!await retirement.WaitAsync(TimeSpan.Zero, TimeProvider.System),
            "a zero graceful wait must report pending retirement");
        Ensure(!completion.Task.IsCompleted,
            "a bounded caller wait must not terminate framework-owned cleanup");

        completion.SetResult();
        await retirement.WaitAsync();
        Ensure(retirement.Completion.IsCompletedSuccessfully,
            "the same retirement handle must observe eventual cleanup completion");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
