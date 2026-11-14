namespace SharpLink.Client;

// One owner per physical Session. Cancellation by a waiter never cancels a shared Bind.
internal sealed class StaticBindingClientState(RpcSession session, StaticBindingBudget ownerBudget)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(long, RpcHash128, StaticBindingScope), (StaticBindingAck Ack, StaticBindingBudget.Lease Lease)> _completed = [];
    private readonly Dictionary<(long, RpcHash128, StaticBindingScope), Operation> _inflight = [];
    private readonly Dictionary<ulong, Operation> _operations = [];
    private readonly uint[] _retiredIncarnations = new uint[StaticBindingWire.Capacity];
    internal Action? InvalidationObservedTestHook;
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private ulong _nextOperation;
    private long _sent;
    private Task _lastPublication = Task.CompletedTask;
    internal long Sent => Interlocked.Read(ref _sent);
    internal int PendingCount { get { lock (_gate) return _operations.Count; } }
    internal int CompletedCount { get { lock (_gate) return _completed.Count; } }
    internal bool IsConfirmedToken(ulong token)
    {
        lock (_gate) return !_closed && !IsRetired(token) && _completed.Values.Any(value => value.Ack.Token == token);
    }
    // Pause actual publication, never replace wire/ack work. Per-state and profile-only.
    internal Func<ulong, StaticBindingPublicationPhase, CancellationToken, ValueTask>? PublicationTestHook;
    internal Action<ulong>? AckObservedTestHook;

    internal Task<StaticBindingAck> BindAsync(long contract, RpcHash128 hash, StaticBindingScope scope = default)
    {
        Operation operation;
        lock (_gate)
        {
            if (_closed || !session.CanAcceptCalls)
                return Task.FromException<StaticBindingAck>(StaticBindingWire.Error(StaticBindingFailure.Unavailable));
            if (!scope.IsValid) return Task.FromException<StaticBindingAck>(StaticBindingWire.Error(StaticBindingFailure.ReceiptMismatch));
            var key = (contract, hash, scope);
            if (_completed.TryGetValue(key, out var completed))
                return Task.FromResult(completed.Ack);
            if (_inflight.TryGetValue(key, out operation!))
                return operation.Completion.Task;
            if (_completed.Count + _inflight.Count >= StaticBindingWire.Capacity || _nextOperation == ulong.MaxValue)
                return Task.FromException<StaticBindingAck>(StaticBindingWire.Error(StaticBindingFailure.Capacity));
            var bindingLease = ownerBudget.TryReserveBinding();
            if (bindingLease is null)
                return Task.FromException<StaticBindingAck>(StaticBindingWire.Error(StaticBindingFailure.Capacity));
            var operationLease = ownerBudget.TryReserveOperation();
            if (operationLease is null)
            {
                bindingLease.Dispose();
                return Task.FromException<StaticBindingAck>(StaticBindingWire.Error(StaticBindingFailure.Capacity));
            }
            var clock = session.RuntimeContext.TimeProvider;
            operation = new Operation(++_nextOperation, contract, hash, scope, _lastPublication,
                RpcDeadline.Create(StaticBindingWire.OperationTimeout, clock.GetTimestamp(), clock.TimestampFrequency),
                bindingLease, operationLease);
            _lastPublication = operation.Published.Task;
            _inflight.Add(key, operation);
            _operations.Add(operation.Id, operation);
        }
        _ = CompleteAsync(operation);
        return operation.Completion.Task;
    }

    private async Task CompleteAsync(Operation operation)
    {
        StaticBindingAck ack = default;
        Exception? failure = null;
        try
        {
            var clock = session.RuntimeContext.TimeProvider;
            var remaining = operation.Deadline.GetRemaining(clock);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Bind publication deadline expired.");
            using var timeout = new CancellationTokenSource(remaining, clock);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(session.LifetimeToken, timeout.Token, _closing.Token);
            try
            {
                await operation.PreviousPublication.WaitAsync(budget.Token).ConfigureAwait(false);
                if (PublicationTestHook is { } before)
                    await before(operation.Id, StaticBindingPublicationPhase.BeforeSend, budget.Token).ConfigureAwait(false);
                await StaticBindingWire.SendBindAsync(session, operation.Id, operation.Contract, operation.Hash,
                    budget.Token, operation.Scope).ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
                if (PublicationTestHook is { } after)
                    await after(operation.Id, StaticBindingPublicationPhase.Enqueued, budget.Token).ConfigureAwait(false);
            }
            finally { operation.Published.TrySetResult(); }
            ack = await operation.Ack.Task.WaitAsync(
                budget.Token).ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            if (ack.Failure != StaticBindingFailure.None)
                throw StaticBindingWire.Error(ack.Failure);
            if (!operation.Scope.MatchesRegistration(operation.Contract, operation.Hash, ack.RegistrationId, ack.Generation))
                throw StaticBindingWire.Malformed("BindAck violates its operation's immutable receipt constraint.");
            lock (_gate)
            {
                if (_closed || !session.CanAcceptCalls)
                    throw StaticBindingWire.Error(StaticBindingFailure.Unavailable);
                if (operation.Deadline.IsExpired(clock))
                    throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Bind operation deadline expired.");
                if (IsRetired(ack.Token)) throw StaticBindingWire.Error(StaticBindingFailure.Retired);
                _completed.Add((operation.Contract, operation.Hash, operation.Scope), (ack, operation.BindingLease));
                operation.BindingCommitted = true;
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _closed) || !session.IsConnected)
        { failure = StaticBindingWire.Error(StaticBindingFailure.Unavailable); }
        catch (OperationCanceledException)
        { failure = new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Bind operation deadline expired."); }
        catch (Exception exception) { failure = exception; }
        finally
        {
            operation.Published.TrySetResult();
            lock (_gate)
            {
                if (!operation.BindingCommitted) operation.BindingLease.Dispose();
                operation.OperationLease.Dispose();
                if (failure is null) operation.Completion.TrySetResult(ack);
                else operation.Completion.TrySetException(failure);
                operation.Finished.TrySetResult();
                _operations.Remove(operation.Id);
                _inflight.Remove((operation.Contract, operation.Hash, operation.Scope));
            }
        }
    }

    internal Task CloseAsync()
    {
        Task[] finished;
        lock (_gate)
        {
            if (_closed) return _drained.Task;
            _closed = true;
            foreach (var completed in _completed.Values) completed.Lease.Dispose();
            _completed.Clear();
            finished = _operations.Values.Select(operation => operation.Finished.Task).ToArray();
        }
        _ = FinishCloseAsync(finished);
        return _drained.Task;
    }

    private bool IsRetired(ulong token)
    {
        var slot = (uint)token;
        if (slot is 0 or > StaticBindingWire.Capacity) return true;
        return (uint)(token >> 32) <= _retiredIncarnations[slot - 1];
    }

    internal void Invalidate(ulong[] tokens)
    {
        lock (_gate)
        {
            foreach (var token in tokens)
            {
                var slot = (uint)token;
                _retiredIncarnations[slot - 1] = Math.Max(_retiredIncarnations[slot - 1], (uint)(token >> 32));
            }
            foreach (var pair in _completed.ToArray())
                if (IsRetired(pair.Value.Ack.Token)) { _completed.Remove(pair.Key); pair.Value.Lease.Dispose(); }
        }
        InvalidationObservedTestHook?.Invoke();
    }

    private async Task FinishCloseAsync(Task[] finished)
    {
        try
        {
            await _closing.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(finished).ConfigureAwait(false);
            _closing.Dispose();
            _drained.TrySetResult();
        }
        catch (Exception exception) { _drained.TrySetException(exception); }
    }

    internal void Receive(ulong id, StaticBindingAck ack)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(id, out var operation))
            {
                // Issued but no longer outstanding: timed out or duplicated. Never republish it.
                if (id == 0 || id > _nextOperation)
                    throw StaticBindingWire.Malformed("BindAck refers to an unissued operation on this Session.");
            }
            else
            {
                if (ack.ContractId != operation.Contract)
                    throw StaticBindingWire.Malformed("BindAck does not match its operation's contract.");
                operation.Ack.TrySetResult(ack);
            }
        }
        AckObservedTestHook?.Invoke(id);
    }

    private sealed class Operation(ulong id, long contract, RpcHash128 hash, StaticBindingScope scope, Task previousPublication, RpcDeadline deadline,
        StaticBindingBudget.Lease bindingLease, StaticBindingBudget.Lease operationLease)
    {
        internal Task PreviousPublication { get; } = previousPublication;
        internal RpcDeadline Deadline { get; } = deadline;
        internal TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StaticBindingBudget.Lease BindingLease { get; } = bindingLease;
        internal StaticBindingBudget.Lease OperationLease { get; } = operationLease;
        internal bool BindingCommitted;
        internal ulong Id { get; } = id;
        internal long Contract { get; } = contract;
        internal RpcHash128 Hash { get; } = hash;
        internal StaticBindingScope Scope { get; } = scope;
        internal TaskCompletionSource<StaticBindingAck> Ack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<StaticBindingAck> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal enum StaticBindingPublicationPhase { BeforeSend, Enqueued }
