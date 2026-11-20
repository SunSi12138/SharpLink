using System.Runtime.CompilerServices;
using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcService]
public sealed class DynamicPluginService : IDynamicPluginService, IAsyncDisposable
{
    private static TaskCompletionSource _blockStarted = NewSignal();
    private static TaskCompletionSource _blockRelease = NewSignal();
    private static CancellationToken _blockCancellation;
    private static TaskCompletionSource _synchronousBlockStarted = NewSignal();
    private static TaskCompletionSource _synchronousBlockRelease = NewSignal();
    private static TaskCompletionSource _rejectResponseStarted = NewSignal();
    private static TaskCompletionSource _rejectResponseRelease = NewSignal();
    private static TaskCompletionSource _serverStreamDisposed = NewSignal();
    private static OutputResource? _outputResource;
    private static int _created;
    private static int _disposed;
    private static int _notifications;
    private static int _inputStreamExits;
    private static int _inputItemsConsumed;
    private static int _notifiedBatchBytes;

    public DynamicPluginService(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        Interlocked.Increment(ref _created);
    }

    public static int Created => Volatile.Read(ref _created);

    public static int Disposed => Volatile.Read(ref _disposed);

    public static int Notifications => Volatile.Read(ref _notifications);
    public static int InputStreamExits => Volatile.Read(ref _inputStreamExits);
    public static int InputItemsConsumed => Volatile.Read(ref _inputItemsConsumed);
    public static int NotifiedBatchBytes => Volatile.Read(ref _notifiedBatchBytes);

    public static Task BlockStarted => Volatile.Read(ref _blockStarted).Task;

    public static CancellationToken BlockCancellation => _blockCancellation;

    public static Task SynchronousBlockStarted => Volatile.Read(ref _synchronousBlockStarted).Task;

    public static Task RejectResponseStarted => Volatile.Read(ref _rejectResponseStarted).Task;

    public static Task ServerStreamDisposed => Volatile.Read(ref _serverStreamDisposed).Task;
    public static Task ServerOutputAwaitingItem => _outputResource!.WaitingForItem.Task;
    public static Task ServerOutputCleanupStarted => _outputResource!.CleanupStarted.Task;
    public static int ServerOutputCleanupCount => _outputResource?.CleanupCount ?? 0;
    public static void HoldServerOutputResource() => _outputResource = new OutputResource();
    public static void FinishServerOutputCleanup() => _outputResource?.CleanupReady.TrySetResult();
    public static void ReleaseServerOutput() => _outputResource?.NextItemReady.TrySetResult();

    public static void Reset()
    {
        Volatile.Write(ref _blockStarted, NewSignal());
        Volatile.Write(ref _blockRelease, NewSignal());
        _blockCancellation = default;
        Volatile.Write(ref _synchronousBlockStarted, NewSignal());
        Volatile.Write(ref _synchronousBlockRelease, NewSignal());
        Volatile.Write(ref _rejectResponseStarted, NewSignal());
        Volatile.Write(ref _rejectResponseRelease, NewSignal());
        Volatile.Write(ref _serverStreamDisposed, NewSignal());
        _outputResource = null;
        Volatile.Write(ref _created, 0);
        Volatile.Write(ref _disposed, 0);
        Volatile.Write(ref _notifications, 0);
        Volatile.Write(ref _inputStreamExits, 0);
        Volatile.Write(ref _inputItemsConsumed, 0);
        Volatile.Write(ref _notifiedBatchBytes, 0);
    }

    public static void ReleaseBlock() => Volatile.Read(ref _blockRelease).TrySetResult();

    public static void ReleaseSynchronousBlock()
        => Volatile.Read(ref _synchronousBlockRelease).TrySetResult();

    public static void ReleaseRejectResponse()
        => Volatile.Read(ref _rejectResponseRelease).TrySetResult();

    public ValueTask<int> UnaryAsync(int value, CancellationToken cancellationToken)
        => ValueTask.FromResult(value + 1);

    public ValueTask NotifyAsync(int value, CancellationToken cancellationToken)
    {
        Interlocked.Add(ref _notifications, value);
        return ValueTask.CompletedTask;
    }

    public ValueTask NoReturnAsync(int value, CancellationToken cancellationToken)
        => NotifyAsync(value, cancellationToken);

    public async ValueTask NotifyStreamAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
    {
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            Interlocked.Add(ref _notifications, value);
    }

    public async ValueTask NotifyBatchAsync(byte[] context, IAsyncEnumerable<byte[]> first,
        IAsyncEnumerable<byte[]> second, CancellationToken cancellationToken)
    {
        var counts = await Task.WhenAll(CountInputAsync(first, cancellationToken),
            CountInputAsync(second, cancellationToken)).ConfigureAwait(false);
        Interlocked.Add(ref _notifiedBatchBytes, context.Length + counts[0] + counts[1]);
    }

    private static async Task<int> CountInputAsync(IAsyncEnumerable<byte[]> items, CancellationToken cancellationToken)
    {
        var total = 0;
        await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
            total += item.Length;
        return total;
    }

    public async ValueTask<int> ClientStreamAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken)
    {
        var sum = 0;
        try
        {
            await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _inputItemsConsumed);
                sum += value;
            }
            return sum;
        }
        finally { Interlocked.Increment(ref _inputStreamExits); }
    }

    public async ValueTask<int> RejectClientStreamAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken)
    {
        _ = values;
        _ = cancellationToken;
        Volatile.Read(ref _rejectResponseStarted).TrySetResult();
        await Volatile.Read(ref _rejectResponseRelease).Task.ConfigureAwait(false);
        return -1;
    }

    public async IAsyncEnumerable<int> ServerStreamAsync(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var output = _outputResource;
        try
        {
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return index;
                if (index == 0 && output is not null)
                {
                    output.WaitingForItem.TrySetResult();
                    await output.NextItemReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                await Task.Yield();
            }
        }
        finally
        {
            if (output is not null)
            {
                output.CleanupStarted.TrySetResult();
                await output.CleanupReady.Task.ConfigureAwait(false);
                Interlocked.Increment(ref output.CleanupCount);
            }
            Volatile.Read(ref _serverStreamDisposed).TrySetResult();
        }
    }

    public async IAsyncEnumerable<int> ThrowingServerStreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return 1;
            await Task.Yield();
            throw new InvalidOperationException("Dynamic service stream failure.");
        }
        finally
        {
            Volatile.Read(ref _serverStreamDisposed).TrySetResult();
        }
    }

    public async IAsyncEnumerable<int> DuplexAsync(
        IAsyncEnumerable<int> values,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _inputItemsConsumed);
                yield return value * 2;
            }
        }
        finally { Interlocked.Increment(ref _inputStreamExits); }
    }

    public async ValueTask<int> BlockAsync(CancellationToken cancellationToken)
    {
        _blockCancellation = cancellationToken;
        Volatile.Read(ref _blockStarted).TrySetResult();
        await Volatile.Read(ref _blockRelease).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return 42;
    }

    public async ValueTask<int> BlockIgnoringCancellationAsync(CancellationToken cancellationToken)
    {
        Volatile.Read(ref _blockStarted).TrySetResult();
        await Volatile.Read(ref _blockRelease).Task.ConfigureAwait(false);
        return 43;
    }

    public ValueTask<int> BlockSynchronously()
    {
        Volatile.Read(ref _synchronousBlockStarted).TrySetResult();
        Volatile.Read(ref _synchronousBlockRelease).Task.GetAwaiter().GetResult();
        return ValueTask.FromResult(44);
    }

    public ValueTask<int> UsePayloadAsync(DynamicPayload payload, CancellationToken cancellationToken)
        => ValueTask.FromResult(
            payload.Value + payload.Label.Length + payload.Values.Sum() +
            (ReferenceEquals(payload, payload.Parent) ? 0 : 1000));

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposed);
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class OutputResource
    {
        internal readonly TaskCompletionSource WaitingForItem = NewSignal();
        internal readonly TaskCompletionSource NextItemReady = NewSignal();
        internal readonly TaskCompletionSource CleanupStarted = NewSignal();
        internal readonly TaskCompletionSource CleanupReady = NewSignal();
        internal int CleanupCount;
    }
}

[RpcService]
public sealed class FirstThrowingDisposalService : IFirstThrowingDisposalService, IAsyncDisposable
{
    private static int _disposed;
    private static int _throwOnDispose;

    public static int Disposed => Volatile.Read(ref _disposed);

    public static void Reset()
    {
        Volatile.Write(ref _disposed, 0);
        Volatile.Write(ref _throwOnDispose, 0);
    }

    public static void EnableDisposeFailure() => Volatile.Write(ref _throwOnDispose, 1);

    public ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken)
        => ValueTask.FromResult(value + 10);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposed);
        if (Volatile.Read(ref _throwOnDispose) != 0)
            throw new InvalidOperationException("First dynamic disposal failure.");
        return ValueTask.CompletedTask;
    }
}

[RpcService]
public sealed class SecondThrowingDisposalService : ISecondThrowingDisposalService, IAsyncDisposable
{
    private static int _disposed;
    private static int _throwOnDispose;

    public static int Disposed => Volatile.Read(ref _disposed);

    public static void Reset()
    {
        Volatile.Write(ref _disposed, 0);
        Volatile.Write(ref _throwOnDispose, 0);
    }

    public static void EnableDisposeFailure() => Volatile.Write(ref _throwOnDispose, 1);

    public ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken)
        => ValueTask.FromResult(value + 20);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposed);
        if (Volatile.Read(ref _throwOnDispose) != 0)
            throw new InvalidOperationException("Second dynamic disposal failure.");
        return ValueTask.CompletedTask;
    }
}

[RpcService(Lifetime = SharpLinkServiceLifetime.Connection)]
public sealed class FlakyConnectionService : IFlakyConnectionService, IAsyncDisposable
{
    private static int _activations;
    private static int _disposed;

    public FlakyConnectionService()
    {
        if (Interlocked.Increment(ref _activations) == 1)
            throw new InvalidOperationException("Transient dynamic activation failure.");
    }

    public static int Activations => Volatile.Read(ref _activations);

    public static int Disposed => Volatile.Read(ref _disposed);

    public static void Reset()
    {
        Volatile.Write(ref _activations, 0);
        Volatile.Write(ref _disposed, 0);
    }

    public ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken)
        => ValueTask.FromResult(value + 30);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposed);
        return ValueTask.CompletedTask;
    }
}

[RpcService(Lifetime = SharpLinkServiceLifetime.Connection)]
public sealed class RetiredConnectionService : IRetiredConnectionService, IAsyncDisposable
{
    private static TaskCompletionSource _disposeStarted = NewSignal();
    private static TaskCompletionSource _disposeRelease = NewSignal();
    private static int _disposed;

    public static Task DisposeStarted => Volatile.Read(ref _disposeStarted).Task;

    public static int Disposed => Volatile.Read(ref _disposed);

    public static void Reset()
    {
        Volatile.Write(ref _disposeStarted, NewSignal());
        Volatile.Write(ref _disposeRelease, NewSignal());
        Volatile.Write(ref _disposed, 0);
    }

    public static void ReleaseDispose() => Volatile.Read(ref _disposeRelease).TrySetResult();

    public ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken)
        => ValueTask.FromResult(value + 40);

    public async ValueTask DisposeAsync()
    {
        Volatile.Read(ref _disposeStarted).TrySetResult();
        await Volatile.Read(ref _disposeRelease).Task.ConfigureAwait(false);
        Interlocked.Increment(ref _disposed);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
