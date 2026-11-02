namespace SharpLink.Client;

/// <summary>Bounded cold control state for exactly one physical Session, never a redirect lineage.</summary>
internal sealed class ClientBindingSession
{
    private readonly RpcSession _session;
    private readonly ClientBindingBudget _budget;
    private readonly Action _onIdentityExhausted;
    private readonly Action _operationOwnerReleased;
    private readonly Action? _controlOwnerReleased;
    private int _activeOperationOwners;
    private readonly int _trackingSlots;
    private readonly Lock _gate = new();
    private readonly Operation?[] _operations;
    private readonly Protocol3IdSequence _ids = new();
    private long _retiredPrefix;
    private bool _closed;
    private bool _admissionClosed;
    private int _queuedControlFrames;
    private bool _trackingSlotsReleased;
    private Task _publicationTail = Task.CompletedTask;
    private readonly CandidateRecord?[] _candidates;
    private readonly List<CleanupOwner> _cleanupOwners;
    private readonly LinkedListNode<ClientBindingSession> _trackingNode;
    private readonly TaskCompletionSource _trackingDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _candidateCount;
    private Task? _terminalDrainTask;
    private bool _terminalDrained;
    private Exception? _terminalFailure;

    internal int TrackingCapacity => _trackingSlots;
    internal Task TrackingDrained => _trackingDrained.Task;
    internal int CandidateCount { get { lock (_gate) return _candidateCount; } }
    internal int CleanupOwnerCount { get { lock (_gate) return _cleanupOwners.Count; } }

    internal ClientBindingSession(RpcSession session, ClientBindingBudget budget, Action onIdentityExhausted,
        Action? controlOwnerReleased = null)
    {
        ArgumentNullException.ThrowIfNull(onIdentityExhausted);
        _session = session;
        _onIdentityExhausted = onIdentityExhausted;
        _operationOwnerReleased = ReleaseOperationOwner;
        _controlOwnerReleased = controlOwnerReleased;
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
            _candidates = new CandidateRecord[_trackingSlots];
            _cleanupOwners = new List<CleanupOwner>(authority.Limits.Operations);
            _trackingNode = budget.RegisterSession(this);
        }
        catch { budget.ReleaseTrackingSlots(_trackingSlots); throw; }
    }

    internal ClientBindingPublication Publication { get; }
    internal int ActiveOperationCount => Volatile.Read(ref _activeOperationOwners);
    internal bool IsCurrentCandidateForTesting(RpcBindingPublication target)
    {
        lock (_gate) return ReferenceEquals(_candidates[Protocol3BindingToken.Slot(target.Token)]?.Target, target);
    }
    internal void ForgetCandidateSiteForTesting(RpcBindingPublication target)
    {
        lock (_gate)
        {
            var record = _candidates[Protocol3BindingToken.Slot(target.Token)];
            if (!ReferenceEquals(record?.Target, target)) throw new InvalidOperationException("Not the current candidate.");
            record!.Site.SetTarget(null!); // Deterministic orphan observation control; no GC timing assertion.
        }
    }
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
            // Close can throw while registering its terminal task. The helper independently
            // joins that already-created physical drain and retains observer/drain failures.
            try { await CloseAndJoinTerminal(expired).ConfigureAwait(false); }
            finally { operation.ReleaseBudget(); }
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
            try { await CloseAndJoinTerminal(exception).ConfigureAwait(false); }
            finally { operation.ReleaseBudget(); }
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
            if (_activeOperationOwners >= _operations.Length ||
                _ids.LastIssued - _retiredPrefix >= _operations.Length)
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

    private void ReserveCleanupOperation()
    {
        lock (_gate)
        {
            if (_closed)
                throw new SharpLinkException(SharpLinkErrorCode.ConnectionClosed,
                    "The exact binding Session is already closed.");
            if (_activeOperationOwners >= _operations.Length || !_budget.TryReserveOperation())
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                    SharpLinkErrorDetails.ResourceExhausted.BindingOperations,
                    "The shared Bind/cleanup operation capacity is unavailable.");
            Interlocked.Increment(ref _activeOperationOwners);
        }
    }

    private void ReleaseOperationOwner()
    {
        lock (_gate)
        {
            if (Interlocked.Decrement(ref _activeOperationOwners) < 0)
                throw new InvalidOperationException("Binding operation ownership was released twice.");
            TryReleaseTrackingSlotsLocked();
        }
    }

    private void TryReleaseTrackingSlotsLocked()
    {
        if (!_closed || !_terminalDrained || _trackingSlotsReleased || _activeOperationOwners != 0 ||
            _candidateCount != 0 || _cleanupOwners.Count != 0 || Volatile.Read(ref _queuedControlFrames) != 0)
            return;
        _trackingSlotsReleased = true;
        _budget.UnregisterSession(_trackingNode);
        _budget.ReleaseTrackingSlots(_trackingSlots);
        if (_terminalFailure is { } failure) _trackingDrained.TrySetException(failure);
        else _trackingDrained.TrySetResult();
    }

    private ValueTask SendReleaseAsync(ulong token, CancellationToken cancellationToken, RpcBindingPublication? publication)
        => SendControlFrameAsync(ProtocolV2FrameType.ReleaseBinding, 0, default, token, cancellationToken, publication);

    private ValueTask SendCancelAsync(long operationId, CancellationToken cancellationToken)
        => SendControlFrameAsync(ProtocolV2FrameType.CancelBind, operationId, default, 0, cancellationToken);

    internal void Close(Exception exception)
    {
        Task? terminal = null;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            Publication.Close();
            foreach (var operation in _operations)
            {
                if (operation is null) continue;
                operation.Completion.TrySetException(exception);
                operation.PublicationConfirmed.TrySetResult();
                // Actual Bind/result owners release only after their physical send cleanup.
            }
            Array.Clear(_operations);
            // Starts with Yield: no disconnect/transport callback can run under the cold gate.
            terminal = _terminalDrainTask = DrainTerminalCandidatesAsync(exception);
        }
        _budget.Observe(terminal, "BindingTerminalDrain");
    }

    private async Task DrainTerminalCandidatesAsync(Exception exception)
    {
        await Task.Yield();
        Exception? failure = null;
        try { await _session.CloseAndDrainOutboundAsync(exception).ConfigureAwait(false); }
        catch (Exception cleanup) { failure = cleanup; }
        // The exact Runtime drain attempts every physical obligation before reporting cleanup
        // failures. Both success and reported post-drain failure therefore end these slot owners.
        lock (_gate)
        {
            for (var slot = 0; slot < _candidates.Length; slot++)
            {
                var record = _candidates[slot];
                if (record is null) continue;
                _candidates[slot] = null;
                _candidateCount--;
                _budget.RemoveCandidateRecord();
                if (record.Cleanup is null) record.Clear();
            }
            _terminalFailure = failure;
            _terminalDrained = true;
            TryReleaseTrackingSlotsLocked();
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task CloseAndJoinTerminal(Exception exception)
    {
        Exception? failure = null;
        try { Close(exception); }
        catch (Exception closeFailure) { failure = closeFailure; }
        Task terminal;
        lock (_gate) terminal = _terminalDrainTask!;
        // Supervision/callback failure cannot skip the already-created exact physical drain.
        try { await terminal.ConfigureAwait(false); }
        catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                RpcPublicationErrors.Combine(exception, failure)!).Throw();
    }

    // Called before PublishIfEligible, never from its publication/site-gated callback.
    internal async Task TrackCandidateAsync(SharpLinkClient.ContractCallSite site, RpcBindingPublication target)
    {
        if (!ReferenceEquals(target.Session, _session))
            throw new InvalidOperationException("A binding candidate cannot cross physical Sessions.");
        CleanupOwner? transferred = null;
        try
        {
            var slot = Protocol3BindingToken.Slot(target.Token);
            if (slot >= _candidates.Length) throw new ArgumentOutOfRangeException(nameof(target));
            var incoming = new CandidateRecord(site, target);
            lock (_gate)
            {
                if (_closed || _budget.TrackingSealed)
                    throw new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Binding tracking is sealed.");
                var previous = _candidates[slot];
                if (ReferenceEquals(previous?.Target, target)) return;
                if (previous is not null && previous.Target!.Token == target.Token)
                    throw Protocol3BindingValidation.State("Duplicate exact token has a different publication control.");
                if (previous is not null && previous.Target!.Token > target.Token)
                {
                    // A delayed, correlated success is legitimate. Its cleanup-only owner must
                    // reserve the shared allowance without changing the newer current slot.
                    transferred = CreateCleanupOwnerLocked(incoming);
                }
                else
                {
                    if (previous is not null)
                    {
                        // Reserve the SAME Bind/cleanup allowance BEFORE replacing the slot reference.
                        if (previous.Cleanup is null) transferred = CreateCleanupOwnerLocked(previous);
                    }
                    else
                    {
                        _candidateCount++;
                        _budget.AddCandidateRecord();
                    }
                    _candidates[slot] = incoming;
                }
            }
            if (transferred is not null) _budget.Observe(transferred.Task, "BindingIncarnationCleanup");
        }
        catch (Exception exception)
        {
            await CloseAndJoinTerminal(exception).ConfigureAwait(false);
            throw;
        }
    }

    internal Task RetireCandidateAsync(RpcBindingPublication target)
        => RetireCandidateAsync(target, out _);

    // Hand the actual existing physical owner to the caller BEFORE supervision can throw.
    // A synchronous observer failure must not conceal a task which has already started.
    internal Task RetireCandidateAsync(RpcBindingPublication target, out Task? originalCleanup)
    {
        originalCleanup = null;
        if (!ReferenceEquals(target.Session, _session))
            throw new InvalidOperationException("A binding retirement cannot cross physical Sessions.");
        CleanupOwner? created = null;
        Task result;
        try
        {
            Publication.Invalidate(target.Token); // Never nest publication -> Session gates.
            lock (_gate)
            {
                if (_closed) return originalCleanup = _terminalDrainTask!;
                var record = _candidates[Protocol3BindingToken.Slot(target.Token)];
                if (!ReferenceEquals(record?.Target, target))
                {
                    // A superseded incarnation already belongs to its bounded cleanup owner.
                    foreach (var owner in _cleanupOwners)
                        if (ReferenceEquals(owner.Record.Target, target)) return originalCleanup = owner.Task;
                    // Completed retirement is idempotent. No second Release marker is published.
                    return originalCleanup = Task.CompletedTask;
                }
                if (record!.Cleanup is { } existing) return originalCleanup = existing.Task;
                created = CreateCleanupOwnerLocked(record);
                result = created.Task;
            }
        }
        catch (Exception exception)
        {
            var fallback = originalCleanup = CloseAndJoinTerminal(exception);
            _budget.Observe(fallback, "BindingCleanupTerminalFallback");
            return fallback;
        }
        originalCleanup = result;
        _budget.Observe(result, "BindingCandidateCleanup");
        return result;
    }

    internal void ExamineCandidate(int slot)
    {
        RpcBindingPublication? orphan = null;
        lock (_gate)
        {
            var record = _candidates[slot];
            if (!_closed && record is not null && record.Cleanup is null &&
                (!record.Site.TryGetTarget(out var site) || site.LocalControl.IsRetired ||
                 !Publication.IsEligible(record.Target!.Token)))
                orphan = record.Target;
        }
        if (orphan is not null) _ = RetireCandidateAsync(orphan);
    }

    private CleanupOwner CreateCleanupOwnerLocked(CandidateRecord record)
    {
        ReserveCleanupOperation(); // Reentrant cold gate; no Bind ID is issued.
        CleanupOwner? owner = null;
        try
        {
            owner = new CleanupOwner(record, _session.RuntimeContext.TimeProvider.GetTimestamp());
            _cleanupOwners.Add(owner); // Preallocated to the negotiated shared operation allowance.
            record.Cleanup = owner;
            owner.Task = RunCandidateCleanupAsync(owner); // Starts with Yield, no I/O under the gate.
            return owner;
        }
        catch
        {
            if (owner is not null) _cleanupOwners.Remove(owner);
            record.Cleanup = null;
            _budget.ReleaseOperation();
            ReleaseOperationOwner();
            throw;
        }
    }

    private async Task RunCandidateCleanupAsync(CleanupOwner owner)
    {
        await Task.Yield();
        RpcBindingPublication? target = owner.Record.Target!;
        Exception? failure = null;
        try
        {
            Publication.Invalidate(target.Token);
            var remaining = TimeSpan.FromMilliseconds(_session.BindingAuthority.Limits.OperationDeadlineMs) -
                _session.RuntimeContext.TimeProvider.GetElapsedTime(owner.Started);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Binding cleanup expired before enqueue.");
            using var timeout = new CancellationTokenSource(remaining, _session.RuntimeContext.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _session.LifetimeToken);
            await SendReleaseAsync(target.Token, linked.Token, target).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            try { await CloseAndJoinTerminal(exception).ConfigureAwait(false); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        }
        finally
        {
            target = null; // Clear the async state's exact target before returning its cold permit.
            lock (_gate)
            {
                var record = owner.Record;
                var slot = Protocol3BindingToken.Slot(record.Target!.Token);
                if (ReferenceEquals(_candidates[slot], record))
                {
                    _candidates[slot] = null;
                    _candidateCount--;
                    _budget.RemoveCandidateRecord();
                }
                // Clear exact references BEFORE returning the control allowance; an old worker
                // can never clear a newer slot incarnation, nor free quota while retaining it.
                record.Clear();
                _cleanupOwners.Remove(owner);
                _budget.ReleaseOperation();
                ReleaseOperationOwner();
            }
        }
        // The last cold owner can make a locally Draining Connection idle. Pool/cluster
        // callbacks run only after exact record/frame ownership and both allowances are returned.
        try { _controlOwnerReleased?.Invoke(); }
        catch (Exception callback) { failure = RpcPublicationErrors.Combine(failure, callback); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class CandidateRecord(SharpLinkClient.ContractCallSite site, RpcBindingPublication target)
    {
        internal readonly WeakReference<SharpLinkClient.ContractCallSite> Site = new(site);
        internal RpcBindingPublication? Target = target;
        internal CleanupOwner? Cleanup;
        internal void Clear()
        {
            Target = null;
            Site.SetTarget(null!);
            Cleanup = null;
        }
    }

    private sealed class CleanupOwner(CandidateRecord record, long started)
    {
        internal CandidateRecord Record { get; } = record;
        internal long Started { get; } = started;
        internal Task Task { get; set; } = Task.CompletedTask;
    }

    private ValueTask SendBindAsync(Operation operation, CancellationToken cancellationToken)
        => SendControlFrameAsync(ProtocolV2FrameType.BindContract, operation.Id,
            operation.Request, 0, cancellationToken);

    private async ValueTask SendControlFrameAsync(ProtocolV2FrameType type, long operationId,
        Protocol3BindRequest request, ulong token, CancellationToken cancellationToken,
        RpcBindingPublication? publication = null)
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
                await _session.SendPacketAndFlushAsync(writer, CancellationToken.None,
                    publication: publication,
                    publicationRole: publication is null ? RpcFramePublicationRole.None : RpcFramePublicationRole.ReleaseBinding).ConfigureAwait(false);
            }
            finally
            {
                if (owns) _session.RuntimeContext.Buffers.Return(writer);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queuedControlFrames);
            lock (_gate) TryReleaseTrackingSlotsLocked();
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
