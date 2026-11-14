namespace SharpLink.Runtime;

// Carries only an already-selected decoder cancellation across physical child release.
// The read loop must not infer ownership again after the pooled call state can disappear.
internal sealed class OneWayInputDecodeTerminalException(OperationCanceledException original)
    : OperationCanceledException("OneWay input decode observed its owning call terminal.", original, original.CancellationToken);

internal sealed partial class StreamManager
{
    internal bool IsOneWayInputRoute(long requestId, ushort streamId)
    {
        var routes = Volatile.Read(ref _dispatchersByRequestId);
        if (routes is null || !routes.TryGetValue(requestId, out var requestRoutes) || !requestRoutes.TryAcquire(streamId, out var entry)) return false;
        try { return entry.Dispatcher is PreAdmissionStreamDispatcher { IsOneWayInput: true }; }
        finally { entry.Release(); }
    }

    internal void ConfigureOneWayInputProgress(long requestId, int count, Func<bool> acceptProgress)
    {
        var routes = Volatile.Read(ref _dispatchersByRequestId);
        if (routes is null || !routes.TryGetValue(requestId, out var requestRoutes)) return;
        for (var index = 1; index <= count; index++)
        {
            if (!requestRoutes.TryAcquire(checked((ushort)index), out var entry)) continue;
            try { if (entry.Dispatcher is PreAdmissionStreamDispatcher mailbox) mailbox.SetOneWayProgress(acceptProgress); }
            finally { entry.Release(); }
        }
    }
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
    private Func<bool>? _oneWayProgress;
    internal bool IsOneWayInput { get { lock (_gate) return _oneWayProgress is not null; } }
    internal void SetOneWayProgress(Func<bool> acceptProgress) { lock (_gate) _oneWayProgress = acceptProgress; }
    private bool AcceptOneWayDecodedProgress()
    {
        Func<bool>? check;
        lock (_gate) check = _oneWayProgress;
        return check?.Invoke() ?? true;
    }
    private bool ClaimOneWayDecodedProgress(int encodedByteCount)
    {
        bool accepted;
        try { accepted = AcceptOneWayDecodedProgress(); }
        catch
        {
            try { NotifyBytesConsumed(encodedByteCount); }
            finally { ReleaseChildDispatch(); }
            throw;
        }
        if (accepted) return true;
        try { NotifyBytesConsumed(encodedByteCount); }
        finally { ReleaseChildDispatch(); }
        return false;
    }
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
