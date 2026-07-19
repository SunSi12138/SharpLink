using System.Runtime.CompilerServices;
using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcService]
public sealed class DynamicPluginService : IDynamicPluginService, IAsyncDisposable
{
    private static TaskCompletionSource _blockStarted = NewSignal();
    private static TaskCompletionSource _blockRelease = NewSignal();
    private static TaskCompletionSource _synchronousBlockStarted = NewSignal();
    private static TaskCompletionSource _synchronousBlockRelease = NewSignal();
    private static int _created;
    private static int _disposed;
    private static int _notifications;

    public DynamicPluginService(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        Interlocked.Increment(ref _created);
    }

    public static int Created => Volatile.Read(ref _created);

    public static int Disposed => Volatile.Read(ref _disposed);

    public static int Notifications => Volatile.Read(ref _notifications);

    public static Task BlockStarted => Volatile.Read(ref _blockStarted).Task;

    public static Task SynchronousBlockStarted => Volatile.Read(ref _synchronousBlockStarted).Task;

    public static void Reset()
    {
        Volatile.Write(ref _blockStarted, NewSignal());
        Volatile.Write(ref _blockRelease, NewSignal());
        Volatile.Write(ref _synchronousBlockStarted, NewSignal());
        Volatile.Write(ref _synchronousBlockRelease, NewSignal());
        Volatile.Write(ref _created, 0);
        Volatile.Write(ref _disposed, 0);
        Volatile.Write(ref _notifications, 0);
    }

    public static void ReleaseBlock() => Volatile.Read(ref _blockRelease).TrySetResult();

    public static void ReleaseSynchronousBlock()
        => Volatile.Read(ref _synchronousBlockRelease).TrySetResult();

    public ValueTask<int> UnaryAsync(int value, CancellationToken cancellationToken)
        => ValueTask.FromResult(value + 1);

    public ValueTask NotifyAsync(int value, CancellationToken cancellationToken)
    {
        Interlocked.Add(ref _notifications, value);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ClientStreamAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken)
    {
        var sum = 0;
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            sum += value;
        return sum;
    }

    public ValueTask<int> RejectClientStreamAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken)
    {
        _ = values;
        _ = cancellationToken;
        return ValueTask.FromResult(-1);
    }

    public async IAsyncEnumerable<int> ServerStreamAsync(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return index;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> DuplexAsync(
        IAsyncEnumerable<int> values,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return value * 2;
    }

    public async ValueTask<int> BlockAsync(CancellationToken cancellationToken)
    {
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
        => ValueTask.FromResult(payload.Value + payload.Label.Length);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposed);
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
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
