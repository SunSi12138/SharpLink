using System.Runtime.ExceptionServices;

namespace SharpLink.Runtime;

internal sealed partial class PooledAsyncStreamDispatcher<T>
{
    // Captured immediately after Rent, before the sink escapes. This scalar is an identity,
    // not a retention: it never permits an old caller to touch a newly rented generation.
    internal long CaptureOriginalLease()
    {
        var lease = Volatile.Read(ref _leaseState);
        if (GetLeaseStatus(lease) != LeaseActive)
            throw new InvalidOperationException("The original stream lease must be captured while active.");
        return lease;
    }

    // The manager must bind the response sink before frames can enter. This joins that exact
    // dispatch state and its credit/abandonment callbacks; it does not join standalone unbound
    // DispatchAsync, nor promise the winner has exited later host-only pool bookkeeping.
    // A loser owns completion only, never the first disposer's error outcome.
    internal ValueTask DisposeOriginalLeaseAsync(long expectedLease, ref bool firstDisposerClaimed)
    {
        if (firstDisposerClaimed)
            throw new ArgumentException("The original-disposer receipt must start empty.", nameof(firstDisposerClaimed));
        if (GetLeaseStatus(expectedLease) != LeaseActive)
            throw new ArgumentException("An active original lease identity is required.", nameof(expectedLease));
        return DisposeLeaseAsync(expectedLease, exactOutcome: true, ref firstDisposerClaimed);
    }

    private ValueTask DisposeLeaseAsync(long expectedLease, bool exactOutcome, ref bool claimed)
    {
        var current = Volatile.Read(ref _leaseState);
        if (!IsSameLeaseGeneration(current, expectedLease))
            return ValueTask.CompletedTask;
        var cleanupThread = Volatile.Read(ref _localAbortCleanupThreadId);
        if (cleanupThread != 0 && cleanupThread == Environment.CurrentManagedThreadId &&
            Volatile.Read(ref _localAbortBufferRetired) == 1)
            throw new InvalidOperationException("A synchronous local-abort credit callback cannot wait for its own dispatcher disposal.");
        if (GetLeaseStatus(current) == LeaseDisposing)
            return AwaitConcurrentDisposeAsync(current);
        if (GetLeaseStatus(current) != LeaseActive || current != expectedLease)
            return ValueTask.CompletedTask;
        var disposing = unchecked(expectedLease + 1);
        var observed = Interlocked.CompareExchange(ref _leaseState, disposing, expectedLease);
        if (observed != expectedLease)
            return IsSameLeaseGeneration(observed, expectedLease) && GetLeaseStatus(observed) == LeaseDisposing
                ? AwaitConcurrentDisposeAsync(observed) : ValueTask.CompletedTask;
        claimed = true; // Definitive outcome ownership before any callback or asynchronous join.
        return DisposeWinnerAsync(disposing, exactOutcome);
    }

    private async ValueTask DisposeWinnerAsync(long disposingLease, bool exactOutcome)
    {
        Exception? failure = null;
        var terminal = Volatile.Read(ref _consumerTerminal);
        var notifyAbandoned = terminal == 0 && Interlocked.CompareExchange(
            ref _consumerTerminal, ConsumerTerminalAbandoned, 0) == 0;
        if (notifyAbandoned)
        {
            _error ??= new OperationCanceledException("The response stream consumer stopped before remote completion.");
            Volatile.Write(ref _completed, true);
        }
        terminal = Volatile.Read(ref _consumerTerminal);
        notifyAbandoned |= terminal == ConsumerTerminalLocalReceiveFailure;
        var terminalPublished = terminal is ConsumerTerminalRemoteComplete or ConsumerTerminalLocalReceiveFailure;
        if (terminalPublished && Volatile.Read(ref _terminalPublicationState) != 2)
        {
            try { await WaitForTerminalPublicationAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                failure = error;
                // A diagnostic hook can fail before installing the actual publication join.
                // Preserve that failure, but independently retain the real publication owner.
                try { await WaitForTerminalPublicationAsync(invokeTestHooks: false).ConfigureAwait(false); }
                catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            }
        }

        Volatile.Write(ref _disposed, true);
        _current = default;
        var dispatchState = Volatile.Read(ref _dispatchState);
        if (dispatchState is not null)
        {
            lock (_dispatchStateGate)
            {
                dispatchState = Volatile.Read(ref _dispatchState) ?? dispatchState;
                try
                {
                    if (terminalPublished) dispatchState.Close();
                    else CloseFirstTerminalDispatchState(dispatchState);
                }
                catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
            }
        }
        try { Signal(); }
        catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }

        // Notification failure cannot release module ownership while a previously acquired
        // manager dispatch is still decoding against this original codec.
        if (dispatchState is not null)
        {
            try { await dispatchState.WaitForDispatchesDrainedAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        }
        try
        {
            if (Volatile.Read(ref _consumerTerminal) == ConsumerTerminalLocalAbort)
                DrainLocalAbortBuffer();
            else
            {
                var discardedBytes = 0;
                while (TryDequeueCore(out _, out var bytes)) discardedBytes = checked(discardedBytes + bytes);
                if (discardedBytes != 0) NotifyBytesConsumed(discardedBytes);
            }
        }
        catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        if (Volatile.Read(ref _consumerTerminal) == ConsumerTerminalLocalAbort)
        {
            try { await WaitForLocalAbortCleanupExitAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        }
        if (notifyAbandoned)
        {
            try
            {
                if (_consumerAbandonedAsync is { } callback)
                    await callback(_consumerAbandonedRequestId, dispatchState).ConfigureAwait(false);
                else _consumerAbandoned?.Invoke(_consumerAbandonedRequestId);
            }
            catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        }
        try { FinalizeDispose(disposingLease); }
        catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        if (failure is not null)
        {
            if (exactOutcome) throw PhysicalTaskFailureException.From(failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
