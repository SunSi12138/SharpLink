namespace SharpLink.Runtime;

internal sealed partial class PreAdmissionStreamDispatcher
{
    private void ReleaseChildDispatch()
    {
        TaskCompletionSource? dispatchesDrained = null;
        lock (_gate)
        {
            if (--_activeChildDispatches < 0)
            {
                _activeChildDispatches++;
                throw new InvalidOperationException("Inbound stream mailbox child dispatch count underflowed.");
            }
            if (_activeChildDispatches == 0)
                dispatchesDrained = TakeDispatchesDrainedCompletionLocked();
        }

        dispatchesDrained?.TrySetResult();
        TryFinalizeChildDetach();
    }

    private void TryFinalizeChildDetach()
    {
        ChildDetachWork? work = null;
        TaskCompletionSource? detachedCompletion;
        lock (_gate)
        {
            if (!_childDetachRequested || _childDetached || _childDetachFinalizing ||
                _attachmentInProgress || _activeChildDispatches != 0)
                return;
            _childDetachFinalizing = true;
            if (_dispatcher is not null)
                work = new ChildDetachWork(_dispatcher, _childLease, _disposeChildOnDetach);
            _dispatcher = null;
        }

        Task? disposal = null;
        Exception? failure = null;
        // Initiate child Dispose before exposing IsDetached, as before. An async child may
        // itself await IsDetached; never await that task before publishing this scalar cut.
        if (work is { DisposeChild: true } disposing)
        {
            try
            {
                if (disposing.Dispatcher is IAsyncDisposable asyncDisposable)
                {
                    var pending = asyncDisposable.DisposeAsync();
                    if (pending.IsCompletedSuccessfully) pending.GetAwaiter().GetResult();
                    else disposal = pending.AsTask(); // Exact original ValueTask consumed once.
                }
                else disposing.Dispatcher.Complete(new OperationCanceledException(
                    "The inbound stream consumer completed before peer terminal."));
            }
            catch (Exception cleanup) { failure = cleanup; }
        }

        lock (_gate)
        {
            _childDetached = true;
            _childDetachFinalizing = false;
            _childLease = null;
            _disposeChildOnDetach = false;
            detachedCompletion = _childDetachedCompletion;
            _childDetachedCompletion = null;
        }
        detachedCompletion?.TrySetResult(); // Self-wait release, NOT physical cleanup proof.
        try { work?.Lease?.OnDispatchesDrained(); }
        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }

        if (disposal is null)
            CompleteChildPhysical(failure); // Includes the no-child synchronous path.
        else
            _ = JoinChildDisposalAsync(disposal, failure); // Owned by the separate physical completion below.
    }

    private async Task JoinChildDisposalAsync(Task disposal, Exception? failure)
    {
        try { await disposal.ConfigureAwait(false); }
        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        CompleteChildPhysical(failure);
    }

    private void RecordChildPhysicalFailure(Exception failure)
    {
        lock (_gate) _childPhysicalFailure = RpcPublicationErrors.Combine(_childPhysicalFailure, failure);
    }

    private void CompleteChildPhysical(Exception? failure)
    {
        TaskCompletionSource? completion;
        lock (_gate)
        {
            _childPhysicalFailure = RpcPublicationErrors.Combine(_childPhysicalFailure, failure);
            _childPhysicalCompleted = true;
            completion = _childPhysicalCompletion;
            failure = _childPhysicalFailure;
        }
        if (failure is null) completion?.TrySetResult();
        else completion?.TrySetException(failure);
    }

    internal ValueTask WaitForPhysicalChildCleanupAsync()
    {
        lock (_gate)
        {
            if (!_localAbandonRequested)
                throw new InvalidOperationException("Physical child cleanup must follow the exact mailbox abandon cut.");
            if (_childPhysicalCompleted)
                return _childPhysicalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(_childPhysicalFailure);
            _childPhysicalCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(_childPhysicalCompletion.Task);
        }
    }
}
