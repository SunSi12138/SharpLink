#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Runtime;

internal sealed partial class StreamManager
{
    // Keep stable discard routes/credit until peer terminal, but join the old typed child
    // before its registration/Codec owner is released. Never allocate a route here.
    internal async ValueTask AbandonOneWayInputsAfterDispatchesAsync(long requestId, int count)
    {
        var routes = Volatile.Read(ref _dispatchersByRequestId);
        if (routes is null || !routes.TryGetValue(requestId, out var requestRoutes)) return;
        for (var index = 1; index <= count; index++)
        {
            var streamId = checked((ushort)index);
            if (!requestRoutes.TryAcquire(streamId, out var entry)) continue;
            ValueTask physicalExit;
            try
            {
                physicalExit = entry.Dispatcher is PreAdmissionStreamDispatcher mailbox
                    ? mailbox.AbandonAndJoinPhysicalAsync() : ValueTask.CompletedTask;
            }
            finally { entry.Release(); }
            await physicalExit.ConfigureAwait(false);
            // A peer terminal that arrived during abandonment can now release the route.
            _ = TryAbandonExistingStream(requestId, streamId);
        }
    }
}

internal sealed partial class PreAdmissionStreamDispatcher
{
    private Task? _physicalChildDisposal;
    internal async ValueTask AbandonAndJoinPhysicalAsync()
    {
        Abandon(out _);
        Task detached;
        lock (_gate)
        {
            if (_childDetached || _dispatcher is null && !_attachmentInProgress && !_childDetachFinalizing && _activeChildDispatches == 0)
                detached = Task.CompletedTask;
            else
            {
                _childDetachedCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                detached = _childDetachedCompletion.Task;
            }
        }
        await detached.ConfigureAwait(false);
        Task? disposal;
        lock (_gate) disposal = _physicalChildDisposal;
        if (disposal is not null) await disposal.ConfigureAwait(false);
    }
}
#endif
