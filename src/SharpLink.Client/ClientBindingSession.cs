namespace SharpLink.Client;

/// <summary>Owns only scalar/control identities for one physical Session.</summary>
internal sealed class ClientBindingSession
{
    private readonly SharpLinkClient _client;
    private readonly ClientConnection _connection;
    private readonly Lock _gate = new();
    private Task _publicationTail = Task.CompletedTask;
    private readonly Dictionary<ContractSiteKey, BindOperation> _operations = [];
    private readonly CompletedBinding?[] _confirmed = new CompletedBinding?[64];
    private readonly uint[] _retired = new uint[64];
    private ulong _lastIssued;
    private int _reservations, _maintenanceOperations;
    private bool _closed;
    private sealed record CompletedBinding(ulong Operation, ContractSiteKey Key, ContractBindAck Ack)
    { internal bool Retiring; internal RpcDeadline ReleaseDeadline; }
    private sealed class BindOperation(ulong id, ContractSiteKey key, RpcDeadline deadline)
    {
        internal Task PreviousPublication { get; set; } = Task.CompletedTask;
        internal TaskCompletionSource Publication { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Published;
        internal ulong Id { get; } = id;
        internal ContractSiteKey Key { get; } = key;
        internal RpcDeadline Deadline { get; } = deadline;
        internal TaskCompletionSource<ContractBindAck> Ack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<BoundContractTarget> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal ClientBindingSession(SharpLinkClient client, ClientConnection connection)
    { _client = client; _connection = connection; }
    internal Task<BoundContractTarget> BindAsync(ContractSiteKey key)
    {
        BindOperation operation;
        lock (_gate)
        {
            if (_closed || !_connection.CanAcceptCalls) return Task.FromException<BoundContractTarget>(ContractBindingWire.Error(ContractBindStatus.Unavailable));
            foreach (var completed in _confirmed)
                if (completed is { Retiring: false } && completed.Key == key)
                    return Task.FromResult(new BoundContractTarget(_connection, _connection.BindingGeneration, completed.Ack.Token));
            if (_operations.TryGetValue(key, out var inflight)) return inflight.Result.Task;
            if (_operations.Count + _maintenanceOperations >= 64 || _reservations >= 64 || _lastIssued == ulong.MaxValue || !_client.BindingQuota.TryReserveBinding())
                return Task.FromException<BoundContractTarget>(ContractBindingWire.Error(ContractBindStatus.Capacity));
            if (!_client.BindingQuota.TryReserveOperation())
            { _client.BindingQuota.ReturnBinding(); return Task.FromException<BoundContractTarget>(ContractBindingWire.Error(ContractBindStatus.Capacity)); }
            try
            {
                operation = new(++_lastIssued, key, RpcDeadline.Create(TimeSpan.FromSeconds(10), _client.BindingTimeProvider));
                operation.PreviousPublication = _publicationTail;
                _operations.Add(key, operation);
                _publicationTail = operation.Publication.Task; _reservations++;
            }
            catch { _client.BindingQuota.ReturnBinding(); _client.BindingQuota.ReturnOperation(); throw; }
        }
        // Registered before bytes or an await can expose the operation. A strong producer root is bounded by the ledger.
        if (!_client.TryStartBindingWork(() => RunOperationAsync(operation), "ContractBindOperation"))
        {
            lock (_gate) { _operations.Remove(key); _reservations--; }
            operation.Publication.TrySetResult();
            _client.BindingQuota.ReturnBinding(); _client.BindingQuota.ReturnOperation();
            operation.Result.TrySetException(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Client is stopping."));
        }
        return operation.Result.Task;
    }
    private async Task RunOperationAsync(BindOperation operation)
    {
        var adopted = false; var exposed = false; var sendOwnsReconciliation = false;
        Exception? failure = null;
        try
        {
            if (!await SharpLinkTimer.WaitAsync(operation.PreviousPublication, operation.Deadline, _client.BindingTimeProvider).ConfigureAwait(false)) throw Deadline();
            if (operation.Deadline.IsExpired(_client.BindingTimeProvider)) throw Deadline();
            var key = operation.Key;
            // Once sending begins, cancellation/unknown enqueue outcome is reconciled by closing this Session.
            exposed = true;
            sendOwnsReconciliation = true;
            await ContractBindingWire.SendAndFlushWithinBudgetAsync(_connection.Session, ProtocolV2FrameType.BindContract, operation.Id,
                ContractBindingWire.WriteRequest(new(key.ContractId, key.Hash, key.Requirement, key.Receipt)), operation.Deadline, _client.BindingTimeProvider, CancellationToken.None).ConfigureAwait(false);
            sendOwnsReconciliation = false;
            lock (_gate) operation.Published = true;
            operation.Publication.TrySetResult();
            if (!await SharpLinkTimer.WaitAsync(operation.Ack.Task, operation.Deadline, _client.BindingTimeProvider).ConfigureAwait(false)) throw Deadline();
            var ack = await operation.Ack.Task.ConfigureAwait(false);
            if (operation.Deadline.IsExpired(_client.BindingTimeProvider)) throw Deadline();
            if (ack.Status != ContractBindStatus.Success) throw ContractBindingWire.Error(ack.Status);
            lock (_gate)
            {
                var slot = checked((int)(uint)ack.Token - 1); var incarnation = (uint)(ack.Token >> 32);
                if (_closed || !_connection.CanAcceptCalls || incarnation <= _retired[slot])
                    throw ContractBindingWire.Error(ContractBindStatus.Retired);
                if (_confirmed[slot] is not null) throw ContractBindingWire.Violation("BindAck reused a live slot.");
                ValidateReceipt(operation.Key, ack);
                _confirmed[slot] = new(operation.Id, operation.Key, ack); adopted = true;
            }
            operation.Result.TrySetResult(new(_connection, _connection.BindingGeneration, ack.Token));
        }
        catch (Exception exception)
        {
            failure = exception is OperationCanceledException ? Deadline() : exception;
            if (exposed && !sendOwnsReconciliation && !(operation.Ack.Task.IsCompletedSuccessfully && operation.Ack.Task.Result.Status != ContractBindStatus.Success))
            {
                _connection.Session.NotifyDisconnected(failure);
                // The normal Session drain joins queued/control writers before reconciliation releases quota.
                try { await _connection.Session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
            }
            operation.Result.TrySetException(failure);
        }
        finally
        {
            operation.Publication.TrySetResult();
            lock (_gate)
            {
                _operations.Remove(operation.Key);
                if (!adopted) { _reservations--; _client.BindingQuota.ReturnBinding(); }
            }
            if (operation.Ack.Task.IsFaulted) _ = operation.Ack.Task.Exception;
            _client.BindingQuota.ReturnOperation();
        }
    }
    private void ValidateReceipt(ContractSiteKey key, ContractBindAck ack)
    {
        if (key.Receipt is not { } receipt) return;
        if (_connection.Session.BindingServerInstanceId != receipt.ServerInstanceId || _connection.Session.BindingRegistryEpoch != receipt.RegistryEpoch ||
            key.Requirement != RpcRegistrationRequirement.Compatible && ack.PublicationGeneration < receipt.Generation ||
            key.Requirement == RpcRegistrationRequirement.ExactRegistration && ack.RegistrationId != receipt.RegistrationId)
            throw ContractBindingWire.Error(ContractBindStatus.ReceiptMismatch);
    }
    private static SharpLinkException Deadline() => new(SharpLinkErrorCode.DeadlineExceeded, "Binding operation deadline exceeded.");
    internal int ReceivedUnpublishedAcks
    { get { lock (_gate) return _operations.Values.Count(static op => !op.Published && op.Ack.Task.IsCompletedSuccessfully); } }
    internal bool IsConfirmed(ContractSiteKey key, ulong token)
    {
        if (!ContractBindingWire.IsToken(token)) return false;
        lock (_gate) return !_closed && _confirmed[(uint)token - 1] is { } entry && !entry.Retiring && entry.Key == key && entry.Ack.Token == token;
    }
    internal void ReceiveAck(ulong id, ContractBindAck ack)
    {
        lock (_gate)
        {
            if (id == 0 || id > _lastIssued) throw ContractBindingWire.Violation("BindAck operation ID is unknown.");
            foreach (var operation in _operations.Values)
            {
                if (operation.Id != id) continue;
                if (operation.Key.ContractId != ack.ContractId) throw ContractBindingWire.Violation("BindAck contract differs from its operation.");
                if (operation.Ack.Task.IsCompletedSuccessfully && operation.Ack.Task.Result != ack)
                    throw ContractBindingWire.Violation("Conflicting BindAck.");
                operation.Ack.TrySetResult(ack); return;
            }
            foreach (var completed in _confirmed)
                if (completed?.Operation == id)
                { if (completed.Ack != ack) throw ContractBindingWire.Violation("Conflicting completed BindAck."); return; }
            // Old terminal operations have no revival path or unbounded tombstone table.
        }
    }
    internal void Invalidate(ulong[] tokens)
    {
        lock (_gate)
        {
            foreach (var token in tokens)
            {
                var slot = checked((int)(uint)token - 1); var incarnation = (uint)(token >> 32);
                _retired[slot] = Math.Max(_retired[slot], incarnation);
                if (_confirmed[slot] is { } completed && completed.Ack.Token == token && !completed.Retiring)
                { _confirmed[slot] = null; _reservations--; _client.BindingQuota.ReturnBinding(); }
            }
        }
        _client.RefreshBindingSites();
    }
    internal void Prune(IReadOnlySet<ContractSiteKey> liveKeys)
    {
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), _client.BindingTimeProvider);
        var releases = new List<CompletedBinding>(64);
        lock (_gate)
        {
            if (_closed) return;
            foreach (var entry in _confirmed)
            {
                if (entry is null || entry.Retiring || liveKeys.Contains(entry.Key)) continue;
                if (_operations.Count + _maintenanceOperations >= 64 || !_client.BindingQuota.TryReserveOperation()) break;
                releases.Add(entry); entry.ReleaseDeadline = deadline; entry.Retiring = true; _maintenanceOperations++;
            }
        }
        foreach (var entry in releases)
            if (!_client.TryStartBindingWork(() => ReleaseAsync(entry), "ContractBindingRelease"))
            {
                lock (_gate) _maintenanceOperations--;
                _client.BindingQuota.ReturnOperation();
            }
    }
    private async Task ReleaseAsync(CompletedBinding entry)
    {
        Exception? failure = null;
        try
        {
            await ContractBindingWire.SendAndFlushWithinBudgetAsync(_connection.Session, ProtocolV2FrameType.BindingRelease, 0,
                ContractBindingWire.WriteRelease(entry.Ack.Token), entry.ReleaseDeadline, _client.BindingTimeProvider, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lock (_gate)
            {
                var slot = checked((int)(uint)entry.Ack.Token - 1);
                if (ReferenceEquals(_confirmed[slot], entry))
                { _confirmed[slot] = null; _reservations--; _client.BindingQuota.ReturnBinding(); }
                _retired[slot] = Math.Max(_retired[slot], (uint)(entry.Ack.Token >> 32));
                _maintenanceOperations--;
            }
            _client.BindingQuota.ReturnOperation();
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal void Close(Exception exception)
    {
        lock (_gate)
        {
            if (_closed) return; _closed = true;
            foreach (var operation in _operations.Values) operation.Ack.TrySetException(exception);
            // Confirmed entries are detached only after the actual Session drain in DisposeAsync.
        }
    }
    internal void CompletePhysicalClose()
    {
        lock (_gate)
        {
            for (var i = 0; i < _confirmed.Length; i++)
                if (_confirmed[i] is not null) { _confirmed[i] = null; _reservations--; _client.BindingQuota.ReturnBinding(); }
        }
    }
}
