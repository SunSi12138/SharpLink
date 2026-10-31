namespace SharpLink.Client;

/// <summary>Bounded cold control state for exactly one physical Session, never a redirect lineage.</summary>
internal sealed class ClientBindingSession
{
    private readonly RpcSession _session;
    private readonly Lock _gate = new();
    private readonly Operation?[] _operations;
    private readonly Protocol3IdSequence _ids = new();
    private long _retiredPrefix;
    private bool _closed;

    internal ClientBindingSession(RpcSession session)
    {
        _session = session;
        var authority = session.BindingAuthority;
        if (!Protocol3BindingLimitsCodec.IsValid(authority.Limits))
            throw new ArgumentException("Session has no negotiated binding limits.", nameof(session));
        _operations = new Operation[authority.Limits.Operations];
        Publication = new ClientBindingPublication(authority.Limits.Slots);
    }

    internal ClientBindingPublication Publication { get; }
    internal Func<Task>? BeforePublicationConfirmationForTesting { get; set; }
    internal Action? EarlyResultObservedForTesting { get; set; }

    internal async ValueTask<Protocol3BindResult> BindAsync(Protocol3BindRequest request)
    {
        Operation operation;
        lock (_gate)
        {
            if (_closed || !_session.CanAcceptCalls)
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                    SharpLinkErrorDetails.Unavailable.SessionBindingDraining, "Session is not accepting binding demands.");
            if (_ids.LastIssued - _retiredPrefix >= _operations.Length)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                    SharpLinkErrorDetails.ResourceExhausted.BindingOperations, "Session binding operation window is full.");
            if (!_ids.TryIssue(out var id))
            {
                _session.MarkDraining();
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                    SharpLinkErrorDetails.Unavailable.SessionBindingDraining, "Session binding ID domain is exhausted.");
            }
            // Install before any enqueue can expose bytes to a fast peer. Timestamp includes queue backpressure.
            operation = new Operation(id, request, _session.RuntimeContext.TimeProvider.GetTimestamp());
            _operations[Index(id)] = operation;
        }

        var budget = TimeSpan.FromMilliseconds(_session.BindingAuthority.Limits.OperationDeadlineMs);
        var enqueueBudget = budget - _session.RuntimeContext.TimeProvider.GetElapsedTime(operation.Started);
        if (enqueueBudget <= TimeSpan.Zero)
        {
            var expired = new TimeoutException("Binding control deadline expired before enqueue.");
            _session.NotifyDisconnected(expired);
            Close(expired);
            throw expired;
        }
        using var timeout = new CancellationTokenSource(enqueueBudget, _session.RuntimeContext.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _session.LifetimeToken);
        try
        {
            await SendBindAsync(operation, linked.Token).ConfigureAwait(false);
            if (BeforePublicationConfirmationForTesting is { } barrier)
                await barrier().WaitAsync(linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_closed)
                    throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "Session closed during Bind publication.");
                operation.Published = true;
                if (operation.EarlyResult is { } early)
                    CompleteLocked(operation, early);
            }
            var remaining = budget - _session.RuntimeContext.TimeProvider.GetElapsedTime(operation.Started);
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Binding control deadline expired.");
            return await operation.Completion.Task.WaitAsync(remaining,
                _session.RuntimeContext.TimeProvider, linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Cancellation of enqueue does not prove bytes never escaped. Close this exact Session;
            // physical send owners still finish through its normal terminal cleanup, with no ID reuse.
            _session.NotifyDisconnected(exception);
            Close(exception);
            throw;
        }
    }

    internal void ReceiveResult(ulong id, ReadOnlySequence<byte> payload)
    {
        var result = Protocol3BindingControlCodec.ReadBindResult(payload, _session.BindingAuthority.Limits.Slots);
        lock (_gate)
        {
            if (_closed)
                return;
            if (id == 0 || id > (ulong)_ids.LastIssued)
                throw Protocol3BindingValidation.State("BindResult refers to an unissued operation.");
            if (id <= (ulong)_retiredPrefix)
                return; // bounded terminal prefix; never resurrect a retired demand
            var operation = _operations[Index((long)id)];
            if (operation is null || operation.Id != (long)id || operation.Completed || operation.EarlyResult.HasValue)
                throw Protocol3BindingValidation.State("Duplicate or uncorrelated BindResult.");
            Protocol3BindingValidation.ValidateResultForRequest(result, operation.Request,
                _session.BindingAuthority, _session.BindingAuthority.Limits.Slots);
            if (result.Status == Protocol3BindStatus.Success)
                Publication.ObserveValidatedSuccess(result.Token);
            if (!operation.Published)
            {
                operation.EarlyResult = result;
                EarlyResultObservedForTesting?.Invoke();
            }
            else
                CompleteLocked(operation, result);
        }
    }

    internal void ReceiveInvalidated(ReadOnlySequence<byte> payload)
    {
        var invalidated = Protocol3BindingControlCodec.ReadBindingInvalidated(payload,
            _session.BindingAuthority.Limits.Slots);
        Publication.Invalidate(invalidated.Token);
    }

    internal async ValueTask ReleaseAsync(ulong token)
    {
        Publication.Invalidate(token);
        var writer = _session.RentFrameWriter();
        var owns = true;
        try
        {
            var packet = writer.BeginPacket(ProtocolV2FrameType.ReleaseBinding, ProtocolV2FrameFlags.None, 0);
            Protocol3BindingControlCodec.WriteReleaseBinding(writer, token, _session.BindingAuthority.Limits.Slots);
            writer.EndPacket(packet);
            owns = false;
            await _session.SendPacketWithBackpressureAsync(writer, _session.LifetimeToken).ConfigureAwait(false);
        }
        finally
        {
            if (owns)
                _session.RuntimeContext.Buffers.Return(writer);
        }
    }

    internal void Close(Exception exception)
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            Publication.Close();
            foreach (var operation in _operations)
                operation?.Completion.TrySetException(exception);
            Array.Clear(_operations);
        }
    }

    private async ValueTask SendBindAsync(Operation operation, CancellationToken cancellationToken)
    {
        var writer = _session.RentFrameWriter();
        var owns = true;
        try
        {
            var packet = writer.BeginPacket(ProtocolV2FrameType.BindContract, ProtocolV2FrameFlags.None, operation.Id);
            Protocol3BindingControlCodec.WriteBindContract(writer, operation.Request);
            writer.EndPacket(packet);
            owns = false;
            await _session.SendPacketWithBackpressureAsync(writer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (owns)
                _session.RuntimeContext.Buffers.Return(writer);
        }
    }

    private void CompleteLocked(Operation operation, Protocol3BindResult result)
    {
        operation.Completed = true;
        operation.EarlyResult = null;
        operation.Completion.TrySetResult(result);
        while (_retiredPrefix < _ids.LastIssued)
        {
            var next = _retiredPrefix + 1;
            var candidate = _operations[Index(next)];
            if (candidate is null || candidate.Id != next || !candidate.Completed)
                break;
            _operations[Index(next)] = null;
            _retiredPrefix = next;
        }
    }

    private int Index(long id) => (int)((ulong)id % (uint)_operations.Length);

    private sealed class Operation(long id, Protocol3BindRequest request, long started)
    {
        internal long Id { get; } = id;
        internal Protocol3BindRequest Request { get; } = request;
        internal long Started { get; } = started;
        internal bool Published;
        internal bool Completed;
        internal Protocol3BindResult? EarlyResult;
        internal TaskCompletionSource<Protocol3BindResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
