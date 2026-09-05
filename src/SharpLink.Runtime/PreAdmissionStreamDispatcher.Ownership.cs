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
        if (work.DisposeChild)
            BeginAbandonedDispatcherDisposal(work.Dispatcher);

        TaskCompletionSource? detachedCompletion;
        lock (_gate)
        {
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

    private static void BeginAbandonedDispatcherDisposal(IStreamDispatcher dispatcher)
    {
        if (dispatcher is not IAsyncDisposable asyncDisposable)
        {
            try
            {
                dispatcher.Complete(new OperationCanceledException(
                    "The inbound stream consumer completed before peer terminal."));
            }
            catch { }
            return;
        }

        try
        {
            var disposal = asyncDisposable.DisposeAsync();
            if (disposal.IsCompletedSuccessfully)
            {
                disposal.GetAwaiter().GetResult();
                return;
            }
            _ = ObserveAbandonedDispatcherDisposalAsync(disposal);
        }
        catch { }
    }

    private static async Task ObserveAbandonedDispatcherDisposalAsync(ValueTask disposal)
    {
        try { await disposal.ConfigureAwait(false); }
        catch { }
    }
}
