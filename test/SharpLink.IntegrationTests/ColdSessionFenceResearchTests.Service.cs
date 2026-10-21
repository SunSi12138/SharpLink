namespace SharpLink.IntegrationTests;

[RpcContract]
public interface IColdFenceResearchService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);
    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync();
    [NonCancellable]
    IAsyncEnumerable<int> DownloadAsync();
    [Sdk.Timeout(0.5)]
    IAsyncEnumerable<int> CompilerDeadlineDownloadAsync(CancellationToken cancellationToken);
    [Sdk.Timeout(0.5)]
    IAsyncEnumerable<int> IgnoreDeadlineDownloadAsync(CancellationToken cancellationToken);
    [NonCancellable]
    IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values);
}

[RpcService]
public sealed partial class ColdFenceResearchService : IColdFenceResearchService
{
    private static TaskCompletionSource s_entered = NewSignal();
    private static TaskCompletionSource s_workRelease = NewSignal();
    private static TaskCompletionSource s_completed = NewSignal();
    private static TaskCompletionSource s_moveNextCompleted = NewSignal();
    private static TaskCompletionSource s_cleanupEntered = NewSignal();
    private static TaskCompletionSource s_cleanupRelease = NewSignal();

    internal static Task Entered => s_entered.Task;
    internal static Task WorkRelease => s_workRelease.Task;
    internal static Task Completed => s_completed.Task;
    internal static Task MoveNextCompleted => s_moveNextCompleted.Task;
    internal static Task CleanupEntered => s_cleanupEntered.Task;
    internal static void ReleaseWork() => s_workRelease.TrySetResult();
    internal static void ReleaseCleanup() => s_cleanupRelease.TrySetResult();
    internal static void ReleaseAll() { ReleaseWork(); ReleaseCleanup(); }

    internal static void Reset()
    {
        s_entered = NewSignal();
        s_workRelease = NewSignal();
        s_completed = NewSignal();
        s_moveNextCompleted = NewSignal();
        s_cleanupEntered = NewSignal();
        s_cleanupRelease = NewSignal();
        ResetCompilerProbe();
    }

    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public async ValueTask NotifyAsync()
    {
        s_entered.TrySetResult();
        await s_workRelease.Task;
        s_completed.TrySetResult();
    }

    public async IAsyncEnumerable<int> DownloadAsync()
    {
        try
        {
            yield return 1;
            await s_workRelease.Task;
            yield return 2;
        }
        finally
        {
            s_cleanupEntered.TrySetResult();
            await s_cleanupRelease.Task;
            s_completed.TrySetResult();
        }
    }

    public IAsyncEnumerable<int> IgnoreDeadlineDownloadAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return new UncooperativeDeadlineStream();
    }

    // A custom iterator makes the two abandoned operations observable independently. Unlike a
    // compiler iterator it deliberately tolerates DisposeAsync while MoveNextAsync is outstanding.
    private sealed class UncooperativeDeadlineStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _moveCount;
        public int Current { get; private set; }
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            if (_moveCount++ == 0)
            {
                Current = 1;
                return ValueTask.FromResult(true);
            }
            return MoveNextIgnoringCancellationAsync();
        }
        private async ValueTask<bool> MoveNextIgnoringCancellationAsync()
        {
            s_entered.TrySetResult();
            await s_workRelease.Task;
            Current = 2;
            s_moveNextCompleted.TrySetResult();
            return true;
        }
        public async ValueTask DisposeAsync()
        {
            s_cleanupEntered.TrySetResult();
            await s_cleanupRelease.Task;
            s_completed.TrySetResult();
        }
    }

    public async IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values)
    {
        try
        {
            await foreach (var value in values)
                yield return value;
        }
        finally
        {
            s_cleanupEntered.TrySetResult();
            await s_cleanupRelease.Task;
            s_completed.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
