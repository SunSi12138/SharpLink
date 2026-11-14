namespace SharpLink.Runtime;

internal sealed partial class PreAdmissionStreamDispatcher
{
    private Task? _physicalChildDisposal;

    internal async ValueTask WaitForAbandonedChildAsync()
    {
        Task? detached;
        lock (_gate)
        {
            detached = _childDetached || (!_attachmentInProgress &&
                _dispatcher is null && !_childDetachFinalizing)
                ? null
                : (_childDetachedCompletion ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        if (detached is not null)
            await detached.ConfigureAwait(false);
        Task? disposal;
        lock (_gate)
            disposal = _physicalChildDisposal;
        if (disposal is not null)
        {
            await disposal.ConfigureAwait(false);
            lock (_gate)
                if (ReferenceEquals(_physicalChildDisposal, disposal))
                    _physicalChildDisposal = null;
        }
    }

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
        ChildDetachWork work;
        lock (_gate)
        {
            if (!_childDetachRequested || _childDetached || _childDetachFinalizing ||
                _attachmentInProgress || _activeChildDispatches != 0 || _dispatcher is null)
            {
                return;
            }

            _childDetachFinalizing = true;
            work = new ChildDetachWork(
                _dispatcher,
                _childLease,
                _disposeChildOnDetach);
            _dispatcher = null;
        }

        // Local abandonment starts disposal before IsDetached becomes visible. A pooled child
        // therefore cannot return/re-rent until this mailbox has performed its final operation.
        var disposal = work.DisposeChild
            ? BeginAbandonedDispatcherDisposal(work.Dispatcher) : null;

        TaskCompletionSource? detachedCompletion;
        lock (_gate)
        {
            _physicalChildDisposal = disposal;
            _childDetached = true;
            _childDetachFinalizing = false;
            _childLease = null;
            _disposeChildOnDetach = false;
            detachedCompletion = _childDetachedCompletion;
            _childDetachedCompletion = null;
        }

        detachedCompletion?.TrySetResult();
        work.Lease?.OnDispatchesDrained();
    }

    private static Task? BeginAbandonedDispatcherDisposal(IStreamDispatcher dispatcher)
    {
        if (dispatcher is not IAsyncDisposable asyncDisposable)
        {
            try
            {
                dispatcher.Complete(new OperationCanceledException(
                    "The inbound stream consumer completed before peer terminal."));
            }
            catch { }
            return null;
        }

        try
        {
            var disposal = asyncDisposable.DisposeAsync();
            if (disposal.IsCompletedSuccessfully)
            {
                disposal.GetAwaiter().GetResult();
                return null;
            }
            return ObserveAbandonedDispatcherDisposalAsync(disposal);
        }
        catch { return null; }
    }

    private static async Task ObserveAbandonedDispatcherDisposalAsync(ValueTask disposal)
    {
        try { await disposal.ConfigureAwait(false); }
        catch { }
    }
}
