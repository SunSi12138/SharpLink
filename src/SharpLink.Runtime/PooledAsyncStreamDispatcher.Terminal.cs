namespace SharpLink.Runtime;

// Keeps receive-terminal publication, including the consumer-disposal handshake,
// isolated from the dispatcher's hot data and pooling paths.
internal sealed partial class PooledAsyncStreamDispatcher<T>
{
    /// <inheritdoc />
    public void Complete(bool isError, string? errorMessage)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage) ? "Remote Error" : errorMessage;
        Complete(isError
            ? new SharpLinkException(SharpLinkErrorCode.RemoteError, message)
            : null);
    }

    /// <inheritdoc />
    public void Complete(Exception? exception)
        => CompleteCore(exception, ConsumerTerminalRemoteComplete);

    // Receive-buffer exhaustion is a local terminal, not a peer StreamComplete. Keep the
    // admitted-buffer draining semantics, but retain abandonment ownership for DisposeAsync.
    private void CompleteLocalReceiveFailure(Exception exception)
        => CompleteCore(exception, ConsumerTerminalLocalReceiveFailure);

    private void CompleteCore(Exception? exception, int terminal)
    {
        var disposition = StreamSinkPublicationDisposition.NotPublished;
        CompleteCore(exception, terminal, ref disposition);
    }

    private void CompleteCore(
        Exception? exception,
        int terminal,
        ref StreamSinkPublicationDisposition disposition)
    {
        if (Interlocked.CompareExchange(ref _consumerTerminal, terminal, 0) != 0)
        {
            ReadExistingSinkPublication(exception, terminal, ref disposition);
            return;
        }

        // The no-state path is the regular server-stream lifecycle. Keep it free of locks and
        // allocations; a consumer that catches the narrow terminal-publication race joins through
        // the lazy completion below instead of observing a partially published terminal state.
        Volatile.Write(ref _terminalPublicationState, 1);
        _error = exception;
        Volatile.Write(ref _completed, true);
        // Caller-owned receipt commits at the actual terminal store, before any notification.
        disposition = StreamSinkPublicationDisposition.Published;
        try
        {
            var dispatchState = Volatile.Read(ref _dispatchState);
            if (dispatchState is null)
            {
                SignalTerminalPublication();
                return;
            }

            lock (_dispatchStateGate)
            {
                if (Volatile.Read(ref _dispatchState) is { } boundDispatchState)
                    CloseFirstTerminalDispatchState(boundDispatchState);
                SignalTerminalPublication();
            }
        }
        finally
        {
            InvokeTestHook(1);

            // Publish only after the first terminal Close and Signal are complete. A racing
            // consumer DisposeAsync then performs its own second Close in sequence.
            Volatile.Write(ref _terminalPublicationState, 2);

            // Fenced re-read (issue #206): a Volatile.Read here can be performed before the
            // release store above becomes globally visible (x86 store-buffer reordering against
            // a cache line the consumer is concurrently reading), so a waiter that installs its
            // completion between the two accesses would miss the signal while its own state
            // re-check still observes 1 — the dispose then hangs forever and strands its
            // NotInParallel constraint key in the test engine. Interlocked.CompareExchange
            // with equal operands is a full-fence read that returns the latest value and
            // writes nothing, so it cannot observe the field before the state store commits.
            Interlocked.CompareExchange(ref _terminalPublication, null, null)?.TrySetResult();
        }
    }

    bool IStreamSinkPublicationReceipt.TryRetainSinkPublication(long expectedLease, out long retainedLease)
        => TryRetainSinkPublication(expectedLease, out retainedLease);

    private bool TryRetainSinkPublication(long expectedLease, out long retainedLease)
    {
        retainedLease = 0;
        var current = Volatile.Read(ref _leaseState);
        if (GetLeaseStatus(expectedLease) != LeaseActive ||
            !IsSameLeaseGeneration(current, expectedLease) ||
            GetLeaseStatus(current) is not (LeaseActive or LeaseDisposing))
            return false;

        // Match the existing acquire/return handshake, but admit this original generation's
        // Disposing state so its consumer-abandonment callback can obtain an exact receipt.
        InvokeTestHook(4);
        Interlocked.Increment(ref _producerOperations);
        current = Volatile.Read(ref _leaseState);
        if (IsSameLeaseGeneration(current, expectedLease) &&
            GetLeaseStatus(current) is LeaseActive or LeaseDisposing)
        {
            retainedLease = expectedLease;
            return true;
        }

        if (Interlocked.Decrement(ref _producerOperations) < 0)
            throw new InvalidOperationException("Stream dispatcher producer lease underflowed.");
        TryReturnToPool();
        return false;
    }

    void IStreamSinkPublicationReceipt.ReleaseSinkPublication(long retainedLease)
        => ReleaseDispatch(retainedLease);

    void IStreamSinkPublicationReceipt.PublishSinkTerminal(
        Exception? error,
        bool localAbort,
        long expectedLease,
        ref StreamSinkPublicationDisposition disposition)
    {
        disposition = StreamSinkPublicationDisposition.NotPublished;
        var current = Volatile.Read(ref _leaseState);
        // The caller must hold the exact retention returned above until this method exits.
        if (GetLeaseStatus(expectedLease) != LeaseActive ||
            !IsSameLeaseGeneration(current, expectedLease) ||
            GetLeaseStatus(current) is not (LeaseActive or LeaseDisposing))
            return;

        if (localAbort)
            CompleteLocalAbortCore(error, expectedLease, ref disposition);
        else
            CompleteCore(error, ConsumerTerminalRemoteComplete, ref disposition);
    }

    private void ReadExistingSinkPublication(
        Exception? error,
        int requestedTerminal,
        ref StreamSinkPublicationDisposition disposition)
    {
        // Acquire the completed store before inspecting its primary. A selected but unfinished
        // publisher is not proof of delivery. The receipt caller's lease prevents pooled reuse.
        if (!Volatile.Read(ref _completed))
            return;
        var terminal = Volatile.Read(ref _consumerTerminal);
        if (terminal == ConsumerTerminalAbandoned)
            disposition = StreamSinkPublicationDisposition.PublishedConsumerAbandonment;
        else if (terminal == requestedTerminal && ReferenceEquals(_error, error))
            disposition = StreamSinkPublicationDisposition.Published;
    }

    private void SignalTerminalPublication()
    {
        InvokeTestHook(9);
        Signal();
    }

    private async ValueTask AwaitTerminalPublicationAndFinishDisposeAsync(
        bool notifyConsumerAbandoned,
        long disposingLeaseState)
    {
        await WaitForTerminalPublicationAsync().ConfigureAwait(false);
        await MarkDisposedAndFinishDisposeAsync(
            notifyConsumerAbandoned,
            terminalPublished: true,
            disposingLeaseState: disposingLeaseState).ConfigureAwait(false);
    }

    private ValueTask WaitForTerminalPublicationAsync()
    {
        if (Volatile.Read(ref _terminalPublicationState) == 2)
            return ValueTask.CompletedTask;

        InvokeTestHook(2);

        var completion = Volatile.Read(ref _terminalPublication);
        if (completion is null)
        {
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = Interlocked.CompareExchange(ref _terminalPublication, created, null) ?? created;
        }

        InvokeTestHook(3);

        // Fenced re-read (issue #206): mirrors the publisher-side fence in Complete. The
        // install CAS above is already a full barrier, but a fenced read keeps the two sides
        // symmetric so neither half of the double-check can observe a stale publication state.
        if (Interlocked.CompareExchange(ref _terminalPublicationState, 0, 0) == 2)
            completion.TrySetResult();

        return new ValueTask(completion.Task);
    }
}
