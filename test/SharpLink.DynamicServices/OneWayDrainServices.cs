using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

public abstract class OneWayDrainService : IAsyncDisposable
{
    private static TaskCompletionSource _handlerEntered = NewSignal();
    private static TaskCompletionSource _handlerRelease = NewSignal();
    private static Task _handlerTask = Task.CompletedTask;
    private static int _disposed;
    public static Task HandlerEntered => _handlerEntered.Task;
    public static Task HandlerTask => Volatile.Read(ref _handlerTask);
    public static int Disposed => Volatile.Read(ref _disposed);
    public static void Reset()
    {
        _handlerEntered = NewSignal();
        _handlerRelease = NewSignal();
        _handlerTask = Task.CompletedTask;
        Volatile.Write(ref _disposed, 0);
    }
    public static void ReleaseHandler() => _handlerRelease.TrySetResult();
    public ValueTask NotifyStreamAsync(IAsyncEnumerable<DrainItem> values, CancellationToken cancellationToken)
        => ReceiveStreamAsync(values, cancellationToken);
    public ValueTask ReceiveStreamAsync(IAsyncEnumerable<DrainItem> values, CancellationToken cancellationToken)
    {
        var task = AwaitDecodeAsync();
        Volatile.Write(ref _handlerTask, task);
        return new ValueTask(task);
    }
    public ValueTask NotifyAsync(CancellationToken cancellationToken)
    {
        var task = AwaitReleaseAsync();
        Volatile.Write(ref _handlerTask, task);
        return new ValueTask(task);
    }
    private static async Task AwaitDecodeAsync()
    {
        _handlerEntered.TrySetResult();
        await DrainItemCodec.DecodeEntered.ConfigureAwait(false);
    }
    private static async Task AwaitReleaseAsync()
    {
        _handlerEntered.TrySetResult();
        await _handlerRelease.Task.ConfigureAwait(false);
    }
    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposed);
        return ValueTask.CompletedTask;
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[RpcService]
public sealed class OneWayDrainSingletonService : OneWayDrainService, IOneWayDrainSingleton;

[RpcService(Lifetime = SharpLinkServiceLifetime.Call)]
public sealed class OneWayDrainCallService : OneWayDrainService, IOneWayDrainCall;

