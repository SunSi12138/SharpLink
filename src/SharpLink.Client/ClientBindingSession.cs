namespace SharpLink.Client;

/// <summary>Bounded cold control state for exactly one physical Session, never a redirect lineage.</summary>
internal sealed class ClientBindingSession
{
    private readonly RpcSession _session;
    private readonly ClientBindingBudget _budget;
    private readonly Action _onIdentityExhausted;
    private readonly Action _operationOwnerReleased;
    private int _activeOperationOwners;
    private readonly int _trackingSlots;
    private readonly Lock _gate = new();
    private readonly Operation?[] _operations;
    private readonly Protocol3IdSequence _ids = new();
    private long _retiredPrefix;
    private bool _closed;
    private bool _admissionClosed;
    private int _queuedControlFrames;
    private Task _publicationTail = Task.CompletedTask;

    internal ClientBindingSession(RpcSession session, ClientBindingBudget budget, Action onIdentityExhausted)
    {
        ArgumentNullException.ThrowIfNull(onIdentityExhausted);
        _session = session;
        _onIdentityExhausted = onIdentityExhausted;
        _operationOwnerReleased = () => Interlocked.Decrement(ref _activeOperationOwners);
        var authority = session.BindingAuthority;
        if (!Protocol3BindingLimitsCodec.IsValid(authority.Limits))
            throw new ArgumentException("Session has no negotiated binding limits.", nameof(session));
        _budget = budget;
        _trackingSlots = checked((int)authority.Limits.Slots);
        if (!budget.TryReserveTrackingSlots(_trackingSlots))
            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                SharpLinkErrorDetails.ResourceExhausted.BindingSessionTracking,
                "Client aggregate Session binding tracking capacity is full.");
        try
        {
            _operations = new Operation[authority.Limits.Operations];
            Publication = new ClientBindingPublication(authority.Limits.Slots);
        }
        catch { budget.ReleaseTrackingSlots(_trackingSlots); throw; }
    }

    internal ClientBindingPublication Publication { get; }
    internal int ActiveOperationCount => Volatile.Read(ref _activeOperationOwners);
    internal Func<Task>? BeforePublicationConfirmationForTesting { get; set; }
    internal Action? EarlyResultObservedForTesting { get; set; }
    internal Action<long>? PublicationWaitStartedForTesting { get; set; }

    internal async ValueTask<ResultOwner> BindAsync(Protocol3BindRequest request, CancellationToken demandRetirement = default)
    {
        if (!Protocol3BindingValidation.IsValid(request))
            throw new ArgumentException("The local binding request is invalid.", nameof(request));
        demandRetirement.ThrowIfCancellationRequested();
        var operation = ReserveOperation(request);

        var budget = TimeSpan.FromMilliseconds(_session.BindingAuthority.Limits.OperationDeadlineMs);
        var enqueueBudget = budget - _session.RuntimeContext.TimeProvider.GetElapsedTime(operation.Started);
        if (enqueueBudget <= TimeSpan.Zero)
        {
            var expired = new TimeoutException("Binding control deadline expired before enqueue.");
            _session.NotifyDisconnected(expired);
            Close(expired);
            operation.ReleaseBudget();
            throw expired;
        }
        try
        {
            // Timer/registration construction is fallible too: it must remain inside the owner cleanup region.
            using var timeout = new CancellationTokenSource(enqueueBudget, _session.RuntimeContext.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _session.LifetimeToken);
            using var retirement = demandRetirement.UnsafeRegister(static state =>
                ((Operation)state!).DemandRetired.TrySetResult(), operation);
            // The send pump is FIFO after enqueue, but capacity waiters need not resume FIFO.
            // Chain the bounded operation records before sending so increasing issued Bind IDs
            // cannot overtake each other while waiting for normal-queue capacity.
            PublicationWaitStartedForTesting?.Invoke(operation.Id);
            await operation.PreviousPublication.WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            await SendBindAsync(operation, linked.Token).ConfigureAwait(false);
            if (BeforePublicationConfirmationForTesting is { } barrier)
                await barrier().WaitAsync(linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_closed)
                    throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "Session closed during Bind publication.");
                operation.Published = true;
                operation.PublicationConfirmed.TrySetResult();
                if (operation.EarlyResult is { } early)
                    CompleteLocked(operation, early);
            }
            var remaining = budget - _session.RuntimeContext.TimeProvider.GetElapsedTime(operation.Started);
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Binding control deadline expired.");
            // A retired factory demand is not an individual Prepare waiter. Its exact creator ID
            // may be cancelled only after Bind publication, on the same normal FIFO. Keep the
            // protocol operation/owner until the final result or Session close.
            if (!operation.Completion.Task.IsCompleted)
            {
                var winner = await Task.WhenAny(operation.Completion.Task, operation.DemandRetired.Task)
                    .WaitAsync(remaining, _session.RuntimeContext.TimeProvider, linked.Token).ConfigureAwait(false);
                if (ReferenceEquals(winner, operation.DemandRetired.Task) && !operation.Completion.Task.IsCompleted)
                    await SendCancelAsync(operation.Id, linked.Token).ConfigureAwait(false);
            }
            remaining = budget - _session.RuntimeContext.TimeProvider.GetElapsedTime(operation.Started);
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Binding control deadline expired during retirement cleanup.");
            var result = await operation.Completion.Task.WaitAsync(remaining,
                _session.RuntimeContext.TimeProvider, linked.Token).ConfigureAwait(false);
            return new ResultOwner(this, result, operation);
        }
        catch (Exception exception)
        {
            // Cancellation of enqueue does not prove bytes never escaped. Close this exact Session;
            // physical send owners still finish through its normal terminal cleanup, with no ID reuse.
            _session.NotifyDisconnected(exception);
            Close(exception);
            operation.ReleaseBudget();
            throw;
        }
    }

    internal void SealAdmission()
    {
        lock (_gate) _admissionClosed = true;
    }

    private Operation ReserveOperation(Protocol3BindRequest request)
    {
        Operation? operation = null;
        Exception? constructionFailure = null;
        var exhausted = false;
        lock (_gate)
        {
            if (_closed || _admissionClosed || !_session.CanAcceptCalls)
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                    SharpLinkErrorDetails.Unavailable.SessionBindingDraining, "Session is not accepting binding demands.");
            if (_ids.LastIssued - _retiredPrefix >= _operations.Length)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                    SharpLinkErrorDetails.ResourceExhausted.BindingOperations, "Session binding operation window is full.");
            if (!_budget.TryReserveOperation())
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                    SharpLinkErrorDetails.ResourceExhausted.BindingOperations,
                    "Client aggregate binding operation capacity is full.");
            if (!_ids.TryIssue(out var id))
            {
                _budget.ReleaseOperation();
                _admissionClosed = true;
                exhausted = true;
            }
            else
            {
                try
                {
                    operation = new Operation(id, request, _session.RuntimeContext.TimeProvider.GetTimestamp(),
                        _budget, _operationOwnerReleased);
                }
                catch (Exception exception)
                {
                    _budget.ReleaseOperation();
                    constructionFailure = exception;
                    _admissionClosed = true;
                }
                if (operation is not null)
                {
                    Interlocked.Increment(ref _activeOperationOwners);
                    operation.PreviousPublication = _publicationTail;
                    _publicationTail = operation.PublicationConfirmed.Task;
                    _operations[Index(id)] = operation;
                }
            }
        }
        // Lifecycle callbacks can acquire pool/cluster gates, so never invoke them while
        // holding the cold operation gate. An issued-but-uninstalled ID closes its Session.
        if (exhausted)
        {
            _onIdentityExhausted();
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable,
                SharpLinkErrorDetails.Unavailable.SessionBindingDraining, "Session binding ID domain is exhausted.");
        }
        if (constructionFailure is not null)
        {
            _session.NotifyDisconnected(constructionFailure);
            Close(constructionFailure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(constructionFailure).Throw();
        }
        return operation!;
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
        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(_session.BindingAuthority.Limits.OperationDeadlineMs),
                _session.RuntimeContext.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _session.LifetimeToken);
            await SendReleaseAsync(token, linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A cancelled enqueue may already be visible. Closing the exact Session releases
            // its server slots without pretending that local cancellation retracts wire bytes.
            _session.NotifyDisconnected(exception);
            Close(exception);
            throw;
        }
    }

    private ValueTask SendReleaseAsync(ulong token, CancellationToken cancellationToken)
        => SendControlFrameAsync(ProtocolV2FrameType.ReleaseBinding, 0, default, token, cancellationToken);

    private ValueTask SendCancelAsync(long operationId, CancellationToken cancellationToken)
        => SendControlFrameAsync(ProtocolV2FrameType.CancelBind, operationId, default, 0, cancellationToken);

    internal void Close(Exception exception)
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            Publication.Close();
            foreach (var operation in _operations)
            {
                if (operation is null) continue;
                operation.Completion.TrySetException(exception);
                operation.PublicationConfirmed.TrySetResult(); // wake ordered publishers into the terminal Session
                operation.ReleaseBudget();
            }
            _budget.ReleaseTrackingSlots(_trackingSlots);
            Array.Clear(_operations);
        }
    }

    private ValueTask SendBindAsync(Operation operation, CancellationToken cancellationToken)
        => SendControlFrameAsync(ProtocolV2FrameType.BindContract, operation.Id,
            operation.Request, 0, cancellationToken);

    private async ValueTask SendControlFrameAsync(ProtocolV2FrameType type, long operationId,
        Protocol3BindRequest request, ulong token, CancellationToken cancellationToken)
    {
        ReserveControlFrame();
        try
        {
            // Control deadlines close this physical Session. Do not cancel a flush waiter and
            // release the frame-count owner while its send-pump frame is still physically queued.
            using var deadline = cancellationToken.UnsafeRegister(static state =>
            {
                var session = (RpcSession)state!;
                session.NotifyDisconnected(new OperationCanceledException("Binding control publication expired or Session closed."));
            }, _session);
            var writer = _session.RentFrameWriter();
            var owns = true;
            try
            {
                var packet = writer.BeginPacket(type, ProtocolV2FrameFlags.None, checked((ulong)operationId));
                switch (type)
                {
                    case ProtocolV2FrameType.BindContract:
                        Protocol3BindingControlCodec.WriteBindContract(writer, request);
                        break;
                    case ProtocolV2FrameType.ReleaseBinding:
                        Protocol3BindingControlCodec.WriteReleaseBinding(writer, token, _session.BindingAuthority.Limits.Slots);
                        break;
                    case ProtocolV2FrameType.CancelBind:
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected client binding control type.");
                }
                writer.EndPacket(packet);
                owns = false;
                await _session.SendPacketAndFlushAsync(writer, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (owns) _session.RuntimeContext.Buffers.Return(writer);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queuedControlFrames);
        }
    }

    private void ReserveControlFrame()
    {
        var maximum = _session.BindingAuthority.Limits.QueuedControlFrames;
        while (true)
        {
            var current = Volatile.Read(ref _queuedControlFrames);
            if (current >= maximum)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                    SharpLinkErrorDetails.ResourceExhausted.BindingControlQueue,
                    "Session binding control-frame capacity is full.");
            if (Interlocked.CompareExchange(ref _queuedControlFrames, current + 1, current) == current)
                return;
        }
    }

    private void CompleteLocked(Operation operation, Protocol3BindResult result)
    {
        operation.Completed = true;
        operation.EarlyResult = null;
        operation.Completion.TrySetResult(result);
        // Result receipt is not protocol retirement. The consumer must publish the route or
        // enqueue cleanup of a successful unpublishable token before the window can advance.
    }

    private void CompleteCleanup(Operation operation)
    {
        lock (_gate)
        {
            if (operation.CleanupCompleted) return;
            operation.CleanupCompleted = true;
            operation.ReleaseBudget();
            if (_closed) return;
            AdvanceRetiredPrefixLocked();
        }
    }

    private void AdvanceRetiredPrefixLocked()
    {
        while (_retiredPrefix < _ids.LastIssued)
        {
            var next = _retiredPrefix + 1;
            var candidate = _operations[Index(next)];
            if (candidate is null || candidate.Id != next || !candidate.Completed || !candidate.CleanupCompleted)
                break;
            _operations[Index(next)] = null;
            _retiredPrefix = next;
        }
    }

    private int Index(long id) => (int)((ulong)id % (uint)_operations.Length);

    internal readonly struct ResultOwner(ClientBindingSession owner, Protocol3BindResult result, Operation operation) : IDisposable
    {
        internal Protocol3BindResult Result { get; } = result;
        public void Dispose() => owner.CompleteCleanup(operation);
    }

    internal sealed class Operation(long id, Protocol3BindRequest request, long started, ClientBindingBudget budget,
        Action? ownerReleased = null)
    {
        private int _budgetReleased;
        internal void ReleaseBudget()
        {
            if (Interlocked.Exchange(ref _budgetReleased, 1) == 0)
            {
                budget.ReleaseOperation();
                ownerReleased?.Invoke(); // atomic accounting only; no lifecycle/registry callback under a gate
            }
        }
        internal long Id { get; } = id;
        internal Protocol3BindRequest Request { get; } = request;
        internal long Started { get; } = started;
        internal bool Published;
        internal bool Completed;
        internal bool CleanupCompleted;
        internal Task PreviousPublication { get; set; } = Task.CompletedTask;
        internal TaskCompletionSource PublicationConfirmed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Protocol3BindResult? EarlyResult;
        internal TaskCompletionSource DemandRetired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Protocol3BindResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
