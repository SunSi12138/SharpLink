namespace SharpLink.Client;

internal sealed partial class ClientStreamPublicationGroup
{
    private const int ReceiptMask = 3;
    private const int ReceiptOpen = 0;
    private const int ReceiptSelected = 1;
    private const int ReceiptPublished = 2;
    private const int ReceiptNotPublished = 3;
    private const int PhysicalReductionClosed = 4;
    private int _logicalReceiptState;
    private PendingCallCompletionReason _logicalReason;
    private Exception? _logicalPrimary;
    private TaskCompletionSource? _logicalReceiptReady;
    private long _responseDispatcherLease;
    // One opaque writer-boundary candidate, NOT proof of a distinct underlying failure.
    // A manual/generated writer may forward a slot fault or independently throw the same raw
    // object. Preserve this bounded diagnostic ambiguity: do not ref-filter/flatten slot errors.
    private Exception? _writerPrimary;
    private Exception? _omissionPrimary;
    internal long ResponseDispatcherLease => _responseDispatcherLease;

    // Replaces Task[count], not a second array/owner. Raw business and independent physical
    // stages are recorded once at actual producer exit, never per stream item.
    private struct ProducerSlot
    {
        internal Task? Task;
        internal Exception? Business;
        internal Exception? Independent;
        internal bool Reported;
    }

    internal static Exception? AddPhysical(Exception? first, Exception? next)
        => first is null ? next : next is null ? first : new AggregateException(first, next);

    // Caller owns the exact PendingCall.CompletionGate. Both existing gates cover provisional
    // selection AND slot removal; readers cannot observe a selection whose CAS then loses.
    internal bool TrySelectTerminal(PendingRequestTable.PendingCall?[] slots, int index,
        PendingRequestTable.PendingCall expected)
    {
        lock (_gate)
        {
            var previous = _logicalReceiptState;
            _logicalReceiptState = (previous & ~ReceiptMask) | ReceiptSelected;
            if (ReferenceEquals(Interlocked.CompareExchange(ref slots[index], null, expected), expected)) return true;
            _logicalReceiptState = previous;
            return false;
        }
    }

    // Called using only captured original values, AFTER terminal processing may have pooled/reset
    // the PendingCall/operation. Accepted is committed before any outside-gate notification.
    internal void FinalizeLogicalReceipt(PendingCallCompletionReason reason, Exception? primary,
        StreamSinkPublicationDisposition disposition, Exception? terminalBatch, ref bool batchAccepted)
    {
        TaskCompletionSource? ready;
        Exception? transferFailure = null;
        lock (_gate)
        {
            try
            {
                if (terminalBatch is not null && (_logicalReceiptState & PhysicalReductionClosed) == 0)
                {
                    var combined = AddPhysical(_registrationFailure, terminalBatch);
                    _registrationFailure = combined;
                    batchAccepted = true;
                }
            }
            catch (Exception error) { transferFailure = error; }
            // Receipt publication is independent of error-batch storage success.
            _logicalReason = reason;
            _logicalPrimary = primary;
            _logicalReceiptState = (_logicalReceiptState & ~ReceiptMask) |
                (disposition is StreamSinkPublicationDisposition.Published or StreamSinkPublicationDisposition.PublishedConsumerAbandonment
                    ? ReceiptPublished : ReceiptNotPublished);
            ready = _logicalReceiptReady;
        }
        try { ready?.TrySetResult(); }
        catch (Exception error) { transferFailure = AddPhysical(transferFailure, error); }
        try { FinalizeSupervisionReceipt(); }
        catch (Exception error) { transferFailure = AddPhysical(transferFailure, error); }
        if (transferFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(transferFailure).Throw();
    }

    internal void ReportProducerOutcome(ushort streamId, Exception? business, Exception? independent)
    {
        lock (_gate)
        {
            ref var slot = ref _tasks[streamId - 1];
            if (slot.Reported) throw new InvalidOperationException("A producer slot reported its physical outcome twice.");
            slot.Business = business;
            slot.Independent = independent;
            slot.Reported = true;
        }
    }

    private void AddIndependent(Exception? error)
    {
        if (error is null) return;
        lock (_gate) _registrationFailure = AddPhysical(_registrationFailure, error);
    }

    private void SelectBusinessFailure(Exception error)
    {
        try { _connection.PendingCalls.TryComplete(RequestId, PendingCallCompletionReason.SendFailure, error); }
        catch (Exception independent) { AddIndependent(independent); }
    }

    private async ValueTask<Exception?> ReduceBusinessAsync(Exception? candidate)
    {
        if (candidate is null) return null;
        Task? wait;
        lock (_gate)
        {
            wait = (_logicalReceiptState & ReceiptMask) == ReceiptSelected
                ? (_logicalReceiptReady ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task : null;
        }
        if (wait is not null) await wait.ConfigureAwait(false);
        lock (_gate)
        {
            var receipt = _logicalReceiptState & ReceiptMask;
            if (receipt == ReceiptPublished &&
                (ReferenceEquals(candidate, _logicalPrimary) ||
                 candidate is OperationCanceledException canceled && canceled.CancellationToken == ProducerToken && ProducerToken.IsCancellationRequested &&
                 _logicalReason != PendingCallCompletionReason.LocalStreamComplete))
                return null;
            if (receipt == ReceiptOpen)
                return AddPhysical(candidate, new InvalidOperationException("A failed producing call has no selected exact logical receipt."));
            return candidate;
        }
    }

    private Exception? ClosePhysicalReduction(Exception? residual)
    {
        lock (_gate)
        {
            var outcome = AddPhysical(residual, _registrationFailure);
            _registrationFailure = null;
            _logicalReceiptState |= PhysicalReductionClosed;
            return outcome;
        }
    }
}
