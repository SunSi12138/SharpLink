using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace StaticStreamCleanup;

[RpcContract]
public interface ICleanupStream : IService
{
    IAsyncEnumerable<int> ReadAsync(CancellationToken cancellationToken);
    [NonCancellable] // No method token; enumeration/transport lifetime remains cancellable.
    IAsyncEnumerable<int> ReadNoTokenAsync();
    [NonCancellable]
    [SharpLink.Sdk.Timeout(3600)]
    IAsyncEnumerable<int> ReadDeadlineAsync();
}

[RpcService(Lifetime = SharpLinkServiceLifetime.Call)]
public sealed class CleanupService : ICleanupStream, IAsyncDisposable
{
    public static readonly TaskCompletionSource<bool> Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static int Started, Completed, Active, ArgumentCancellable, EffectiveCancellable;
    public static int CancellationObserved, MoveNextUnwound, DisposeStarted, DisposeCompleted, Overlap;
    public static int InMoveNext, ServiceDisposed;
    public static bool Hold;
    public static readonly TaskCompletionSource<bool> WorkRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static readonly TaskCompletionSource<bool> DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static readonly TaskCompletionSource<bool> DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref ServiceDisposed);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<int> ReadAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref ArgumentCancellable, cancellationToken.CanBeCanceled ? 1 : 0);
        return new ObservedEnumerable(Core(cancellationToken));
    }

    public IAsyncEnumerable<int> ReadNoTokenAsync() => new ObservedEnumerable(Core(CancellationToken.None));
    public IAsyncEnumerable<int> ReadDeadlineAsync() => new ObservedEnumerable(Core(CancellationToken.None));

    private static async IAsyncEnumerable<int> Core([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Started);
        Interlocked.Increment(ref Active);
        Volatile.Write(ref EffectiveCancellable, cancellationToken.CanBeCanceled ? 1 : 0);
        using var registration = cancellationToken.Register(() => Interlocked.Increment(ref CancellationObserved));
        try
        {
            yield return 0;
            Waiting.TrySetResult(true);
            if (Hold)
            {
                // Deliberately ignore cancellation until the controller releases physical work.
                await WorkRelease.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            else
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref Active);
            Interlocked.Increment(ref Completed);
        }
    }

    private sealed class ObservedEnumerable(IAsyncEnumerable<int> inner) : IAsyncEnumerable<int>
    {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new ObservedEnumerator(inner.GetAsyncEnumerator(cancellationToken));
    }

    private sealed class ObservedEnumerator(IAsyncEnumerator<int> inner) : IAsyncEnumerator<int>
    {
        private int moves;
        public int Current => inner.Current;
        public async ValueTask<bool> MoveNextAsync()
        {
            var ordinal = Interlocked.Increment(ref moves);
            Interlocked.Increment(ref InMoveNext);
            try { return await inner.MoveNextAsync().ConfigureAwait(false); }
            finally
            {
                // The held second MoveNext must unwind before disposal. This witness
                // is independent of the scheduling/order of cancellation callbacks.
                if (ordinal == 2)
                    Interlocked.Increment(ref MoveNextUnwound);
                Interlocked.Decrement(ref InMoveNext);
            }
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeStarted);
            if (Volatile.Read(ref InMoveNext) != 0 || Volatile.Read(ref MoveNextUnwound) != 1)
                Interlocked.Increment(ref Overlap);
            DisposeEntered.TrySetResult(true);
            if (Hold) await DisposeRelease.Task.ConfigureAwait(false);
            await inner.DisposeAsync().ConfigureAwait(false);
            Interlocked.Increment(ref DisposeCompleted);
        }
    }
}
