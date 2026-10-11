namespace SharpLink.Runtime;

internal sealed partial class StreamFlowController
{
    private readonly int _sendGrantQuantum;
    private int _sendGrantPressure;
    // A conservative scan hint, never a credit balance: local consumption does not update it.
    private bool _mayHaveUnusedSendGrants;
    private static long _grantActivated, _grantDisposed, _grantFallbacks;
    private static long _grantLocalItems, _grantRefills, _grantSlowItems, _grantRecalled, _grantRefunded;

    internal bool TryAcquireSendCreditLease(
        long requestId, ushort streamId, int encodedBytes, CancellationToken cancellationToken,
        out SendCreditLease? lease)
    {
        ValidateEncodedBytes(encodedBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var acquired = TryAcquireSendCreditCore(new StreamKey(requestId, streamId), encodedBytes, true, out lease);
        if (!acquired)
            Interlocked.Increment(ref _grantFallbacks);
        return acquired;
    }

    private bool TryAcquireSendCreditCore(StreamKey key, int encodedBytes, bool capture, out SendCreditLease? lease)
    {
        lease = null;
        lock (_gate)
        {
            ThrowIfTerminated();
            RecallSendGrantsIfNeeded(encodedBytes);
            if (!_sendStates.TryGetValue(key, out var state))
            {
                // A failed pre-credit probe must not register a state or join the waiter queue.
                if (_waiters.Count != 0 || !HasConnectionCredit(_sendConnectionCredit, encodedBytes))
                    return false;
                if (_sendStates.Count < _maxConcurrentStreams)
                    state = AddSendState(key);
                else if (_activeSendStreamCount >= _maxConcurrentStreams)
                    throw CreateConcurrentStreamLimitException();
                else
                    return false;
            }

            if (capture)
                state.Gate ??= new Lock();
            using var stateLock = new SendStateLock(state);
            if (state.AbortException is { } abortException)
                throw abortException;
            if (capture && state.CurrentLease is not null)
                return false;
            if (state.Completed)
                throw CreateStreamClosedException();
            RecallSendGrant(state);
            if (_waiters.Count != 0 || !CanReserve(state.Credit, _sendConnectionCredit, encodedBytes))
                return false;
            if (capture)
            {
                lease = new SendCreditLease(this, state, key, state.Lease);
                state.CurrentLease = lease;
                Interlocked.Increment(ref _grantActivated);
            }
            // The first item is exact; a short stream never reserves a speculative grant.
            Reserve(state, encodedBytes);
            return true;
        }
    }

    private bool TryAcquireLeaseCredit(SendCreditLease lease, int encodedBytes)
    {
        lock (_gate)
        {
            using (new SendStateLock(lease.State))
            {
                lease.ThrowIfUnavailable();
                RecallSendGrant(lease.State);
                if (_waiters.Count != 0 || HasConnectionCredit(_sendConnectionCredit, encodedBytes))
                    return TryReserveLeaseCredit(lease, encodedBytes);
            }
            // Never recall another generation's grant on behalf of an already stale holder.
            // No state gate is held while the authority visits the other streams.
            RecallIdleSendGrants();
            using var stateLock = new SendStateLock(lease.State);
            lease.ThrowIfUnavailable();
            return TryReserveLeaseCredit(lease, encodedBytes);
        }
    }

    private bool TryReserveLeaseCredit(SendCreditLease lease, int encodedBytes)
    {
        var state = lease.State;
        if (_waiters.Count == 0 && CanReserve(state.Credit, _sendConnectionCredit, encodedBytes))
        {
            if (lease.GrantEnabled && encodedBytes <= _sendGrantQuantum &&
                encodedBytes <= state.Credit && encodedBytes <= _sendConnectionCredit)
            {
                // Wc = F + sum(Gi + Di), Ws = Ci + Di. Refill debits F, never Ci.
                // Q includes this item; only its n bytes become actual stream debt.
                var grant = Math.Min(_sendGrantQuantum, Math.Min(state.Credit, _sendConnectionCredit));
                _sendConnectionCredit -= grant;
                state.UnusedGrant = grant - encodedBytes;
                state.Credit -= encodedBytes;
                _mayHaveUnusedSendGrants |= state.UnusedGrant != 0;
                lease.OwnerRefills++;
                return true;
            }
            lease.ExactSlowItems++;
            Reserve(state, encodedBytes);
            return true;
        }
        // Counts exact slow-path attempts, including attempts canceled before admission.
        lease.ExactSlowItems++;
        return false;
    }

    private void RecallSendGrantsIfNeeded(int encodedBytes)
    {
        if (!HasConnectionCredit(_sendConnectionCredit, encodedBytes))
            RecallIdleSendGrants();
    }

    private void RecallIdleSendGrants()
    {
        // The owner publishes pressure before acquiring any local gate. A local item already
        // inside its gate can linearize first; no producer must run to make idle Gi reclaimable.
        Volatile.Write(ref _sendGrantPressure, 1);
        if (_mayHaveUnusedSendGrants)
        {
            foreach (var state in _sendStates.Values)
            {
                using var stateLock = new SendStateLock(state);
                RecallSendGrant(state);
            }
            _mayHaveUnusedSendGrants = false;
        }
        Volatile.Write(ref _sendGrantPressure, _waiters.Count == 0 ? 0 : 1);
    }

    private void RecallSendGrant(SendState state)
    {
        var grant = state.UnusedGrant;
        if (grant == 0)
            return;
        _sendConnectionCredit = checked(_sendConnectionCredit + grant);
        state.UnusedGrant = 0;
        Debug.Assert(_sendConnectionCredit <= _connectionWindow);
        if (state.CurrentLease is { } lease)
            lease.RecalledGrantBytes += grant;
    }

    private void ReleaseSendLease(SendCreditLease lease, bool dispose)
    {
        List<CreditWaiter>? rejected = null;
        List<CreditWaiter>? ready;
        lock (_gate)
        {
            using (new SendStateLock(lease.State))
            {
                lease.GrantEnabled = false;
                if (_sendStates.TryGetValue(lease.Key, out var state) && lease.IsCurrent(state))
                    RecallSendGrant(state);
                if (dispose)
                    lease.PublishEvidence();
            }
            if (dispose)
            {
                for (var node = _waiters.First; node is not null;)
                {
                    var next = node.Next;
                    if (ReferenceEquals(node.Value.GrantLease, lease))
                    {
                        RemoveWaiter(node.Value);
                        node.Value.Rejection = lease.TerminalException ?? _terminalException ?? CreateStreamClosedException();
                        (rejected ??= []).Add(node.Value);
                    }
                    node = next;
                }
            }
            ready = AdmitWaiters();
        }
        CompleteReadyWaiters(rejected);
        CompleteReadyWaiters(ready);
    }

    // Lock order is owner -> at most one state. A local miss releases state before owner entry.
    // Gate is lazy and retained across pooling; legacy-only states need no additional Lock.
    private readonly struct SendStateLock : IDisposable
    {
        private readonly Lock? _lock;
        internal SendStateLock(SendState? state)
        {
            _lock = state?.Gate;
            _lock?.Enter();
        }
        public void Dispose() => _lock?.Exit();
    }

    internal sealed class SendState(long initialCredit)
    {
        public long Credit = initialCredit;
        public bool Completed;
        public Exception? AbortException;
        public bool CreditRefundedByAbort;
        public long Lease;
        public SendState? Next;
        public Lock? Gate;
        public long UnusedGrant;
        public SendCreditLease? CurrentLease;
    }

    // One context per generation, never pooled: a stale holder retains its selected exception
    // and Closed bit after its SendState has been retired and rented to another logical stream.
    internal sealed class SendCreditLease : IDisposable
    {
        private readonly StreamFlowController _owner;
        internal readonly SendState State;
        internal readonly StreamKey Key;
        private readonly long _epoch;
        internal bool GrantEnabled = true;
        internal bool Closed;
        internal Exception? TerminalException;
        internal long LocalItems, OwnerRefills, ExactSlowItems, RecalledGrantBytes, RefundedItemBytes;
        private bool _disposed, _evidencePublished;

        internal SendCreditLease(StreamFlowController owner, SendState state, StreamKey key, long epoch)
        {
            _owner = owner;
            State = state;
            Key = key;
            _epoch = epoch;
        }

        internal bool IsCurrent(SendState state)
            => ReferenceEquals(State, state) && state.Lease == _epoch && ReferenceEquals(state.CurrentLease, this);

        internal Exception? GetFailure()
        {
            if (TerminalException is { } selected)
                return selected;
            if (Volatile.Read(ref _owner._terminalException) is { } terminal)
                return terminal;
            if (Closed || !IsCurrent(State))
                return CreateStreamClosedException();
            return State.AbortException ?? (State.Completed ? CreateStreamClosedException() : null);
        }

        internal void ThrowIfUnavailable()
        {
            if (GetFailure() is { } error)
                throw error;
        }

        private bool TryLocal(int encodedBytes, CancellationToken cancellationToken)
        {
            _owner.ValidateEncodedBytes(encodedBytes);
            cancellationToken.ThrowIfCancellationRequested();
            lock (State.Gate!)
            {
                ThrowIfUnavailable();
                if (!GrantEnabled || Volatile.Read(ref _owner._sendGrantPressure) != 0 ||
                    State.UnusedGrant < encodedBytes || State.Credit < encodedBytes)
                    return false;
                State.UnusedGrant -= encodedBytes;
                State.Credit -= encodedBytes;
                LocalItems++;
                return true;
            }
        }

        internal ValueTask AcquireAsync(int encodedBytes, CancellationToken cancellationToken)
        {
            if (TryLocal(encodedBytes, cancellationToken) || _owner.TryAcquireLeaseCredit(this, encodedBytes))
                return ValueTask.CompletedTask;
            // Admission transfers n to the existing item helper. No post-admission closed or
            // cancellation check may revoke that transfer without returning the reserved bytes.
            return _owner.AcquireContendedSendCreditAsync(Key, State, _epoch, encodedBytes, cancellationToken, this);
        }

        internal bool TryAcquire(int encodedBytes, CancellationToken cancellationToken)
            => TryLocal(encodedBytes, cancellationToken) || _owner.TryAcquireLeaseCredit(this, encodedBytes);

        internal void ReturnUnsent(int credit) => _owner.ReturnUnsentCreditCore(Key, credit, this);
        internal void DisableGrant() => _owner.ReleaseSendLease(this, false);
        internal void Complete(Exception? exception = null) => _owner.CompleteSendStreamCore(Key, exception, this);

        internal void Close(Exception? exception)
        {
            TerminalException ??= exception;
            Closed = true;
            GrantEnabled = false;
        }

        public void Dispose()
        {
            lock (State.Gate!)
            {
                if (_disposed)
                    return;
                _disposed = true;
                Close(null); // Publish before waiting for the authority; never refund an admitted n here.
            }
            _owner.ReleaseSendLease(this, true);
        }

        internal void RecordRefund(int credit)
        {
            // An admitted item can settle after Dispose. Only that successful late cleanup uses
            // a global atomic; ordinary items and ignored post-abort refunds never do so.
            if (_evidencePublished)
                Interlocked.Add(ref _grantRefunded, credit);
            else
                RefundedItemBytes += credit;
        }

        internal void PublishEvidence()
        {
            if (_evidencePublished)
                return;
            _evidencePublished = true;
            Interlocked.Increment(ref _grantDisposed);
            Interlocked.Add(ref _grantLocalItems, LocalItems);
            Interlocked.Add(ref _grantRefills, OwnerRefills);
            Interlocked.Add(ref _grantSlowItems, ExactSlowItems);
            Interlocked.Add(ref _grantRecalled, RecalledGrantBytes);
            Interlocked.Add(ref _grantRefunded, RefundedItemBytes);
        }
    }

    internal readonly record struct SendGrantEvidence(
        long ActivatedStreams, long DisposedStreams, long FirstProbeFallbacks, long LocalItems,
        long OwnerRefills, long ExactSlowItems, long RecalledGrantBytes, long RefundedItemBytes);

    internal static SendGrantEvidence ReadSendGrantEvidence() => new(
        Volatile.Read(ref _grantActivated), Volatile.Read(ref _grantDisposed), Volatile.Read(ref _grantFallbacks),
        Volatile.Read(ref _grantLocalItems), Volatile.Read(ref _grantRefills), Volatile.Read(ref _grantSlowItems),
        Volatile.Read(ref _grantRecalled), Volatile.Read(ref _grantRefunded));

    internal readonly record struct SendGrantSnapshot(
        long ConnectionCredit, long StreamCredit, long UnusedGrant, long Lease,
        bool HasState, bool Completed, bool LeaseClosed, bool GrantEnabled, bool Pressure,
        int ActiveStreams, int RetainedStreams, int WaiterCount, object? StateIdentity,
        SendCreditLease? CurrentLease, long TotalUnusedGrant, long TotalDebt);

    internal SendGrantSnapshot CaptureSendGrantSnapshot(long requestId, ushort streamId)
    {
        lock (_gate)
        {
            var key = new StreamKey(requestId, streamId);
            long streamCredit = 0, unused = 0, epoch = 0, totalGrant = 0, totalDebt = 0;
            bool completed = false, closed = false, enabled = false;
            SendState? selected = null;
            SendCreditLease? selectedLease = null;
            foreach (var pair in _sendStates)
            {
                var state = pair.Value;
                using var stateLock = new SendStateLock(state);
                // Local consumption changes G and debt by opposite amounts. F is fixed under
                // owner, so this diagnostic scan needs no second balance or simultaneous locks.
                totalGrant += state.UnusedGrant;
                totalDebt += _streamWindow - state.Credit;
                if (pair.Key != key)
                    continue;
                selected = state;
                streamCredit = state.Credit;
                unused = state.UnusedGrant;
                epoch = state.Lease;
                completed = state.Completed;
                selectedLease = state.CurrentLease;
                closed = selectedLease?.Closed ?? false;
                enabled = selectedLease?.GrantEnabled ?? false;
            }
            return new(_sendConnectionCredit, streamCredit, unused, epoch, selected is not null,
                completed, closed, enabled, _sendGrantPressure != 0, _activeSendStreamCount,
                _sendStates.Count, _waiters.Count, selected, selectedLease, totalGrant, totalDebt);
        }
    }

    private SendState AddSendState(StreamKey key)
    {
        var state = RentSendState();
        _sendStates.Add(key, state);
        _activeSendStreamCount++;
        return state;
    }

    private SendState RentSendState()
    {
        var state = _pooledSendStates;
        if (state is null)
        {
            state = new SendState(_streamWindow);
        }
        else
        {
            var next = state.Next;
            _pooledSendStates = next;
            if (next is not null)
            {
                Debug.Assert(_pooledSendStateOverflowCount > 0);
                _pooledSendStateOverflowCount--;
            }
            else
            {
                Debug.Assert(_pooledSendStateOverflowCount == 0);
            }

            state.Next = null;
        }

        using var stateLock = new SendStateLock(state);
        state.Credit = _streamWindow;
        state.Completed = false;
        state.AbortException = null;
        state.CreditRefundedByAbort = false;
        state.UnusedGrant = 0;
        state.CurrentLease = null;
        state.Lease = ++_sendStateLeaseCounter;
        return state;
    }

    private void RemoveSendState(StreamKey key, SendState state)
    {
        if (_sendStates.Remove(key))
            ReturnSendState(state);
    }

    private void ReturnSendState(SendState state)
    {
        Debug.Assert(state.Completed);
        Debug.Assert(state.Credit == _streamWindow);
        Debug.Assert(state.Next is null);
        Debug.Assert(state.UnusedGrant == 0);
        var pooledState = _pooledSendStates;
        if (pooledState is null)
        {
            Debug.Assert(_pooledSendStateOverflowCount == 0);
            _pooledSendStates = state;
            return;
        }

        if (_pooledSendStateOverflowCount >= _maxPooledSendStates - 1)
            return;

        state.Next = pooledState;
        _pooledSendStates = state;
        _pooledSendStateOverflowCount++;
    }

    private void ClearPooledSendStates()
    {
        var state = _pooledSendStates;
        _pooledSendStates = null;
        _pooledSendStateOverflowCount = 0;
        while (state is not null)
        {
            var next = state.Next;
            state.Next = null;
            state = next;
        }
    }

}
