using System.Runtime.CompilerServices;
using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcService]
public sealed class PhysicalTailAuditService : IPhysicalTailAudit, IAsyncDisposable
{
    public static string Mode { get; set; } = "MoveNext";
    public static TaskCompletionSource MoveEntered { get; } = Signal();
    public static TaskCompletionSource MoveRelease { get; } = Signal();
    public static TaskCompletionSource MoveExited { get; } = Signal();
    public static TaskCompletionSource DisposeEntered { get; } = Signal();
    public static TaskCompletionSource DisposeRelease { get; } = Signal();
    public static TaskCompletionSource DisposeExited { get; } = Signal();
    public static int ActiveMove;
    public static int ActiveDispose;
    public static int ServiceDisposed;
    public static int TailSawDisposedService;
    public static CancellationToken CallToken;
    public static CancellationToken EnumeratorToken;

    public async IAsyncEnumerable<TailAuditItem> WarmupAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new(7);
        await Task.CompletedTask;
    }
    public IAsyncEnumerable<TailAuditItem> ServerAsync(CancellationToken cancellationToken)
    {
        CallToken = cancellationToken;
        return new HeldTail();
    }
    public IAsyncEnumerable<TailAuditItem> DuplexAsync(IAsyncEnumerable<int> input, CancellationToken cancellationToken)
        => ServerAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref ServiceDisposed);
        return ValueTask.CompletedTask;
    }
    public static void Release()
    {
        MoveRelease.TrySetResult();
        DisposeRelease.TrySetResult();
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class HeldTail : IAsyncEnumerable<TailAuditItem>, IAsyncEnumerator<TailAuditItem>
    {
        public TailAuditItem Current => new(8);
        public IAsyncEnumerator<TailAuditItem> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            EnumeratorToken = cancellationToken;
            return this;
        }
        public async ValueTask<bool> MoveNextAsync()
        {
            Interlocked.Increment(ref ActiveMove);
            MoveEntered.TrySetResult();
            try
            {
                // Real non-cooperative user operation, not a framework hook.
                await MoveRelease.Task.ConfigureAwait(false);
                if (Volatile.Read(ref ServiceDisposed) != 0) Interlocked.Increment(ref TailSawDisposedService);
                return false;
            }
            finally { Interlocked.Decrement(ref ActiveMove); MoveExited.TrySetResult(); }
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref ActiveDispose);
            DisposeEntered.TrySetResult();
            try
            {
                if (Mode == "Dispose")
                {
                    // Isolate disposal: let the abandoned MoveNext exit while Dispose remains held.
                    MoveRelease.TrySetResult();
                    await DisposeRelease.Task.ConfigureAwait(false);
                    if (Volatile.Read(ref ServiceDisposed) != 0) Interlocked.Increment(ref TailSawDisposedService);
                }
            }
            finally { Interlocked.Decrement(ref ActiveDispose); DisposeExited.TrySetResult(); }
        }
    }
}
