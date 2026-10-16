#if SHARPLINK_STATIC_BINDING_PROTOTYPE
namespace SharpLink.Client;

// One owner per physical Session. Cancellation by a waiter never cancels a shared Bind.
internal sealed class StaticBindingClientState(RpcSession session)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(long, RpcHash128), StaticBindingAck> _completed = [];
    private readonly Dictionary<(long, RpcHash128), Operation> _inflight = [];
    private readonly Dictionary<ulong, Operation> _operations = [];
    private ulong _nextOperation;
    private long _sent;
    internal long Sent => Interlocked.Read(ref _sent);
    internal int PendingCount { get { lock (_gate) return _operations.Count; } }

    internal Task<StaticBindingAck> BindAsync(long contract, RpcHash128 hash)
    {
        Operation operation;
        lock (_gate)
        {
            if (!session.CanAcceptCalls)
                return Task.FromException<StaticBindingAck>(StaticBindingWire.Error(StaticBindingFailure.Unavailable));
            var key = (contract, hash);
            if (_completed.TryGetValue(key, out var completed))
                return Task.FromResult(completed);
            if (_inflight.TryGetValue(key, out operation!))
                return operation.Completion.Task;
            if (_completed.Count + _inflight.Count >= StaticBindingWire.Capacity || _nextOperation == ulong.MaxValue)
                return Task.FromException<StaticBindingAck>(StaticBindingWire.Error(StaticBindingFailure.Capacity));
            operation = new Operation(++_nextOperation, contract, hash);
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
            StaticBindingWire.SendBind(session, operation.Id, operation.Contract, operation.Hash);
            Interlocked.Increment(ref _sent);
            ack = await operation.Ack.Task.WaitAsync(
                StaticBindingWire.OperationTimeout, session.LifetimeToken).ConfigureAwait(false);
            if (ack.Failure != StaticBindingFailure.None)
                throw StaticBindingWire.Error(ack.Failure);
            lock (_gate)
            {
                if (!session.CanAcceptCalls)
                    throw StaticBindingWire.Error(StaticBindingFailure.Unavailable);
                _completed.Add((operation.Contract, operation.Hash), ack);
            }
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            lock (_gate)
            {
                _operations.Remove(operation.Id);
                _inflight.Remove((operation.Contract, operation.Hash));
                if (!session.IsConnected) _completed.Clear();
            }
        }
        if (failure is null) operation.Completion.TrySetResult(ack);
        else operation.Completion.TrySetException(failure);
    }

    internal void Receive(ulong id, StaticBindingAck ack)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(id, out var operation))
            {
                // Issued but no longer outstanding: timed out or duplicated. Never republish it.
                if (id != 0 && id <= _nextOperation)
                    return;
                throw StaticBindingWire.Malformed("BindAck refers to an unissued operation on this Session.");
            }
            if (ack.ContractId != operation.Contract)
                throw StaticBindingWire.Malformed("BindAck does not match its operation's contract.");
            operation.Ack.TrySetResult(ack);
        }
    }

    private sealed class Operation(ulong id, long contract, RpcHash128 hash)
    {
        internal ulong Id { get; } = id;
        internal long Contract { get; } = contract;
        internal RpcHash128 Hash { get; } = hash;
        internal TaskCompletionSource<StaticBindingAck> Ack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<StaticBindingAck> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
#endif
