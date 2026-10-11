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
        if (Interlocked.CompareExchange(ref _consumerTerminal, terminal, 0) != 0)
            return;

        // The no-state path is the regular server-stream lifecycle. Keep it free of locks and
        // allocations; a consumer that catches the narrow terminal-publication race joins through
        // the lazy completion below instead of observing a partially published terminal state.
        Volatile.Write(ref _terminalPublicationState, 1);
        _error = exception;
        Volatile.Write(ref _completed, true);
        try
        {
            var dispatchState = Volatile.Read(ref _dispatchState);
            if (dispatchState is null)
            {
                Signal();
                return;
            }

            lock (_dispatchStateGate)
            {
                if (Volatile.Read(ref _dispatchState) is { } boundDispatchState)
                    CloseFirstTerminalDispatchState(boundDispatchState);
                Signal();
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
