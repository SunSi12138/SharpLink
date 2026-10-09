namespace SharpLink.Runtime;

internal sealed partial class StreamFlowController
{
    // First-use admission returns the lease captured while reserving credit. Resolving
    // the key after releasing the gate (especially after an await) can bind a replacement
    // lifecycle to the old debit. Existing keyed entry points remain unchanged for callers
    // that do not retain a lease, and the steady-state resolved hot path is unaffected.
    internal bool TryAcquireSendCreditLease(
        long requestId,
        ushort streamId,
        int encodedBytes,
        out ResolvedSendCreditLease lease)
    {
        ValidateEncodedBytes(encodedBytes);
        var key = new StreamKey(requestId, streamId);
        lease = default;
        lock (_gate)
        {
            ThrowIfTerminated();
            if (!_sendStates.TryGetValue(key, out var state))
            {
                // Match the keyed probe: unsuccessful first use must not publish a state.
                if (_waiters.Count != 0 || !HasConnectionCredit(_sendConnectionCredit, encodedBytes))
                    return false;
                if (_sendStates.Count < _maxConcurrentStreams)
                    state = AddSendState(key);
                else if (_activeSendStreamCount >= _maxConcurrentStreams)
                    throw CreateConcurrentStreamLimitException();
                else
                    return false;
            }

            if (state.AbortException is { } abortException)
                throw abortException;
            if (state.Completed)
                throw CreateStreamClosedException();

            lease = new ResolvedSendCreditLease(this, requestId, streamId, state, state.Lease);
            if (_waiters.Count != 0 || !CanReserve(state.Credit, _sendConnectionCredit, encodedBytes))
                return false;

            Reserve(state, encodedBytes);
            return true;
        }
    }

    internal ValueTask<ResolvedSendCreditLease> AcquireSendCreditLeaseAsync(
        long requestId,
        ushort streamId,
        int encodedBytes,
        CancellationToken cancellationToken)
    {
        ValidateEncodedBytes(encodedBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var key = new StreamKey(requestId, streamId);
        CreditWaiter waiter;
        List<CreditWaiter>? ready;
        lock (_gate)
        {
            ThrowIfTerminated();
            if (!_sendStates.TryGetValue(key, out var state))
            {
                if (_sendStates.Count < _maxConcurrentStreams)
                    state = AddSendState(key);
                else if (_activeSendStreamCount >= _maxConcurrentStreams)
                    throw CreateConcurrentStreamLimitException();
            }

            if (state is not null)
            {
                if (state.AbortException is { } abortException)
                    throw abortException;
                if (state.Completed)
                    throw CreateStreamClosedException();
                if (_waiters.Count == 0 && CanReserve(state.Credit, _sendConnectionCredit, encodedBytes))
                {
                    Reserve(state, encodedBytes);
                    return new ValueTask<ResolvedSendCreditLease>(
                        new ResolvedSendCreditLease(this, requestId, streamId, state, state.Lease));
                }
            }

            var waitsForStateCapacity = state is null;
            if (waitsForStateCapacity && _pendingSendStateWaiterCount >= MaxPendingSendStateWaiters)
                throw CreatePendingStreamCapacityLimitException();

            waiter = new CreditWaiter(
                this,
                key,
                encodedBytes,
                waitsForStateCapacity,
                expectedState: state,
                expectedLease: state?.Lease ?? 0L);
            if (waitsForStateCapacity)
                _pendingSendStateWaiterCount++;
            waiter.Node = _waiters.AddLast(waiter);
            ready = AdmitWaiters();
        }

        CompleteReadyWaiters(ready);
        return AwaitFirstAdmissionLeaseAsync(waiter, cancellationToken);
    }

    private static async ValueTask<ResolvedSendCreditLease> AwaitFirstAdmissionLeaseAsync(
        CreditWaiter waiter,
        CancellationToken cancellationToken)
    {
        await waiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        // AdmitWaiters fixes these fields before publishing successful completion. Never
        // re-read state.Lease here: the pooled object may already belong to a new stream.
        var state = waiter.ExpectedState ??
            throw new InvalidOperationException("A successful send admission must own a stream state.");
        return new ResolvedSendCreditLease(
            waiter.Owner,
            waiter.Key.RequestId,
            waiter.Key.StreamId,
            state,
            waiter.ExpectedLease);
    }

    internal ResolvedSendCreditLease ResolveSendCreditLease(long requestId, ushort streamId)
    {
        var key = new StreamKey(requestId, streamId);
        lock (_gate)
        {
            ThrowIfTerminated();
            if (!_sendStates.TryGetValue(key, out var state) ||
                state.Completed ||
                state.AbortException is not null)
            {
                return default;
            }

            return new ResolvedSendCreditLease(
                this,
                key.RequestId,
                key.StreamId,
                state,
                state.Lease);
        }
    }

}
