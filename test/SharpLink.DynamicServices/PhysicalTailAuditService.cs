using System.Runtime.CompilerServices;
using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

public abstract class PhysicalTailAuditServiceBase : IAsyncDisposable
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
    public static int OtherServiceDisposed;
    public static int ServiceDisposeFailures;
    public static bool ThrowOnServiceDispose { get; set; }
    public static int TailSawDisposedService;
    public static CancellationToken CallToken;
    public static CancellationToken EnumeratorToken;
    public static int EnumeratorDisposals;
    public static int CompilerFinallyEntries;
    public static bool CompilerLateFault { get; set; }
    private bool _isTailService;
    private int _disposed;

    public async IAsyncEnumerable<TailAuditItem> WarmupAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new(7);
        await Task.CompletedTask;
    }
    public IAsyncEnumerable<TailAuditItem> ServerAsync(CancellationToken cancellationToken)
    {
        CallToken = cancellationToken;
        _isTailService = true;
        return new HeldTail(this);
    }
    public IAsyncEnumerable<TailAuditItem> DuplexAsync(IAsyncEnumerable<int> input, CancellationToken cancellationToken)
        => ServerAsync(cancellationToken);

    public IAsyncEnumerable<TailAuditItem> CompilerAsync(CancellationToken cancellationToken)
    {
        CallToken = cancellationToken;
        _isTailService = true;
        return new CountedCompilerStream(CompilerCoreAsync());
    }

    private async IAsyncEnumerable<TailAuditItem> CompilerCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnumeratorToken = cancellationToken;
        Interlocked.Increment(ref ActiveMove);
        MoveEntered.TrySetResult();
        var moveExited = false;
        try
        {
            await MoveRelease.Task.ConfigureAwait(false); // deliberately ignores cancellation
            if (Volatile.Read(ref _disposed) != 0) Interlocked.Increment(ref TailSawDisposedService);
            Interlocked.Decrement(ref ActiveMove);
            moveExited = true;
            MoveExited.TrySetResult();
            if (CompilerLateFault) throw new InvalidOperationException("late compiler MoveNext failure");
            yield return new(9); // must never reach serialization after logical termination
        }
        finally
        {
            if (!moveExited) { Interlocked.Decrement(ref ActiveMove); MoveExited.TrySetResult(); }
            Interlocked.Increment(ref CompilerFinallyEntries);
            Interlocked.Increment(ref ActiveDispose);
            DisposeEntered.TrySetResult();
            try
            {
                await DisposeRelease.Task.ConfigureAwait(false);
                if (Volatile.Read(ref _disposed) != 0) Interlocked.Increment(ref TailSawDisposedService);
            }
            finally { Interlocked.Decrement(ref ActiveDispose); DisposeExited.TrySetResult(); }
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposed);
        if (_isTailService)
            Interlocked.Increment(ref ServiceDisposed);
        else
            Interlocked.Increment(ref OtherServiceDisposed);
        if (_isTailService && ThrowOnServiceDispose)
        {
            Interlocked.Increment(ref ServiceDisposeFailures);
            throw new InvalidOperationException("PHYSICAL_TAIL_SERVICE_DISPOSE_FAILURE");
        }
        return ValueTask.CompletedTask;
    }
    public static void Release()
    {
        MoveRelease.TrySetResult();
        DisposeRelease.TrySetResult();
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static void ReleaseMove() => MoveRelease.TrySetResult();
    private sealed class CountedCompilerStream(IAsyncEnumerable<TailAuditItem> inner) : IAsyncEnumerable<TailAuditItem>
    {
        public IAsyncEnumerator<TailAuditItem> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new CountedEnumerator(inner.GetAsyncEnumerator(cancellationToken));
        private sealed class CountedEnumerator(IAsyncEnumerator<TailAuditItem> enumerator) : IAsyncEnumerator<TailAuditItem>
        {
            public TailAuditItem Current => enumerator.Current;
            public ValueTask<bool> MoveNextAsync() => enumerator.MoveNextAsync();
            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref EnumeratorDisposals);
                return enumerator.DisposeAsync();
            }
        }
    }
    private sealed class HeldTail(PhysicalTailAuditServiceBase owner) : IAsyncEnumerable<TailAuditItem>, IAsyncEnumerator<TailAuditItem>
    {
        private CancellationTokenRegistration _cancellation;
        public TailAuditItem Current => new(8);
        public IAsyncEnumerator<TailAuditItem> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            EnumeratorToken = cancellationToken;
            if (Mode == "Dispose")
                _cancellation = cancellationToken.Register(() => MoveRelease.TrySetResult());
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
                if (Volatile.Read(ref owner._disposed) != 0) Interlocked.Increment(ref TailSawDisposedService);
                return false;
            }
            finally { Interlocked.Decrement(ref ActiveMove); MoveExited.TrySetResult(); }
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref EnumeratorDisposals);
            Interlocked.Increment(ref ActiveDispose);
            DisposeEntered.TrySetResult();
            try
            {
                if (Mode == "Dispose")
                {
                    // Cancellation already released the non-target MoveNext.
                    await DisposeRelease.Task.ConfigureAwait(false);
                    if (Volatile.Read(ref owner._disposed) != 0) Interlocked.Increment(ref TailSawDisposedService);
                }
            }
            finally { _cancellation.Dispose(); Interlocked.Decrement(ref ActiveDispose); DisposeExited.TrySetResult(); }
        }
    }
}

[RpcService]
public sealed class PhysicalTailAuditService : PhysicalTailAuditServiceBase, IPhysicalTailAudit;

[RpcService(Lifetime = SharpLinkServiceLifetime.Call)]
public sealed class PhysicalTailCallAuditService : PhysicalTailAuditServiceBase, IPhysicalTailCallAudit;
