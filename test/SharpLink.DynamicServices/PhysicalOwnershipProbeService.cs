using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcService(Lifetime = SharpLinkServiceLifetime.Call)]
public sealed class PhysicalOwnershipProbeService : IPhysicalOwnershipProbe, IAsyncDisposable
{
    private static TaskCompletionSource s_workStarted = NewSignal();
    private static TaskCompletionSource s_workRelease = NewSignal();
    private static TaskCompletionSource s_workFinished = NewSignal();
    private static TaskCompletionSource s_disposeStarted = NewSignal();
    private static TaskCompletionSource s_disposeRelease = NewSignal();
    private static TaskCompletionSource s_disposeFinished = NewSignal();
    private static CancellationToken s_workCancellation;
    private static int s_serviceDisposed;
    private static int s_concurrentDispose;

    public static Task WorkStarted => s_workStarted.Task;
    public static Task WorkFinished => s_workFinished.Task;
    public static Task DisposeStarted => s_disposeStarted.Task;
    public static Task DisposeFinished => s_disposeFinished.Task;
    public static int WorkCancelled => s_workCancellation.IsCancellationRequested ? 1 : 0;
    public static int ServiceDisposed => Volatile.Read(ref s_serviceDisposed);
    public static int ConcurrentDispose => Volatile.Read(ref s_concurrentDispose);

    public static void Reset()
    {
        s_workStarted = NewSignal();
        s_workRelease = NewSignal();
        s_workFinished = NewSignal();
        s_disposeStarted = NewSignal();
        s_disposeRelease = NewSignal();
        s_disposeFinished = NewSignal();
        s_workCancellation = default;
        s_serviceDisposed = 0;
        s_concurrentDispose = 0;
    }

    public static void ReleaseWork() => s_workRelease.TrySetResult();
    public static void ReleaseDispose() => s_disposeRelease.TrySetResult();
    public static void ReleaseAll() { ReleaseWork(); ReleaseDispose(); }

    public IAsyncEnumerable<int> DeadlineStreamAsync(CancellationToken cancellationToken)
        => new HeldStream(cooperative: false);

    public IAsyncEnumerable<int> CooperativeStreamAsync(CancellationToken cancellationToken)
        => new HeldStream(cooperative: true);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref s_serviceDisposed);
        return ValueTask.CompletedTask;
    }

    private sealed class HeldStream(bool cooperative) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private CancellationToken _cancellation;
        private int _moveCount;
        public int Current { get; private set; }

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _cancellation = cancellationToken;
            s_workCancellation = cancellationToken;
            return this;
        }

        public ValueTask<bool> MoveNextAsync()
        {
            if (_moveCount++ == 0)
            {
                Current = 1;
                return ValueTask.FromResult(true);
            }
            return WaitForWorkAsync();
        }

        private async ValueTask<bool> WaitForWorkAsync()
        {
            s_workStarted.TrySetResult();
            try
            {
                if (cooperative)
                    await s_workRelease.Task.WaitAsync(_cancellation).ConfigureAwait(false);
                else
                    await s_workRelease.Task.ConfigureAwait(false);
                Current = 2;
                return true;
            }
            finally
            {
                s_workFinished.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!s_workFinished.Task.IsCompleted)
                Interlocked.Increment(ref s_concurrentDispose);
            s_disposeStarted.TrySetResult();
            await s_disposeRelease.Task.ConfigureAwait(false);
            s_disposeFinished.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
