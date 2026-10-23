using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcService]
public sealed class MaintenanceNotificationProbe : IMaintenanceNotificationProbe, IAsyncDisposable
{
    private static readonly TaskCompletionSource InvocationEnteredSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource CallbackEnteredSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource CallbackExitedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource InvocationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource CallbackRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _disposed;
    private static int _overlappingDisposal;

    public static Task InvocationEntered => InvocationEnteredSource.Task;
    public static Task CallbackEntered => CallbackEnteredSource.Task;
    public static Task CallbackExited => CallbackExitedSource.Task;
    public static int Disposed => Volatile.Read(ref _disposed);
    public static int OverlappingDisposal => Volatile.Read(ref _overlappingDisposal);
    public static void ReleaseInvocation() => InvocationRelease.TrySetResult();
    public static void ReleaseCallback() => CallbackRelease.TrySetResult();

    public ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ConsumeAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
    {
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            _ = value;
    }

    public async ValueTask<int> ReleaseBeforeCancellationCallbackAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
    {
        // Leave registration lifetime with the original call token. Disposing it here would
        // itself join the callback and hide the framework's separate physical ownership cut.
        _ = cancellationToken.Register(static () =>
        {
            CallbackEnteredSource.TrySetResult();
            try { CallbackRelease.Task.GetAwaiter().GetResult(); }
            finally { CallbackExitedSource.TrySetResult(); }
        });
        await using var enumerator = values.GetAsyncEnumerator();
        var input = enumerator.MoveNextAsync().AsTask();
        InvocationEnteredSource.TrySetResult();
        await InvocationRelease.Task.ConfigureAwait(false);
        try { _ = await input.ConfigureAwait(false); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
        return 29;
    }

    public ValueTask DisposeAsync()
    {
        if (CallbackEntered.IsCompleted && !CallbackExited.IsCompleted)
            Volatile.Write(ref _overlappingDisposal, 1);
        Interlocked.Increment(ref _disposed);
        return ValueTask.CompletedTask;
    }
}
