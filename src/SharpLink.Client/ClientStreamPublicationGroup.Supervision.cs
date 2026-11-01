namespace SharpLink.Client;

// One pre-admitted anchor on the existing group/gate. Admission remains the existing unbounded
// producing-invocation admission; only this owner's task/branch state is bounded, not live count.
internal sealed partial class ClientStreamPublicationGroup
{
    private TaskCompletionSource? _supervisionCompletion;
    private FrameworkTaskRegistration? _supervisionRegistration;
    private Task? _originTask;
    private Task? _responseDrainTask;
    private Exception? _supervisionFailure;
    private bool _originAssigned;
    private bool _originExited;
    private bool _logicalFinalized;
    private bool _responseDrainClaimed;
    private bool _responseDrainAssigned;
    private bool _responseDrainExited;
    private bool _supervisionFinished;
    private const int ResponseRoutePhaseMask = 3;
    private const int OriginSinkUseDone = 1 << 2;
    private const int PendingSinkUseDone = 1 << 3;
    private const int OriginalSinkRetentionClaimed = 1 << 4;
    // Low bits: 0=none, 1=prepared, 2=publish attempted, 3=settled. Upper bits reuse
    // this existing scalar for the single original dispatcher registration-retention handoff.
    private int _responseRoutePublication;
    private TaskCompletionSource? _responseRouteSettled;
    private bool _selectionOwned;
    private bool _registrationClaimed;
    private RpcBindingPublication? _registrationBinding;
    private IRpcOperation? _registeredOperation;

    internal bool IsAdopted => HasProducerLifetime;
    internal ClientConnection? PreparedConnection => _registrationBinding is null ? null : _connection;
    internal bool HasSupervisionAnchor => _supervisionCompletion is not null;
    internal Task SupervisionTaskForDiagnostics => _supervisionCompletion?.Task ?? Task.CompletedTask;
    internal bool HasOriginRootForDiagnostics { get { lock (_gate) return _originTask is not null; } }
    internal int ProducerTaskRootCountForDiagnostics
    {
        get { lock (_gate) { var count = 0; foreach (var slot in _tasks) if (slot.Task is not null) ++count; return count; } }
    }
    internal bool HasResponseDrainRootForDiagnostics { get { lock (_gate) return _responseDrainTask is not null; } }

    internal ClientStreamPublicationGroup(int streamCount) : base(checked((ushort)streamCount))
    {
        if (streamCount is < 1 or > RpcMethodFacts.MaxClientStreamCount)
            throw new ArgumentOutOfRangeException(nameof(streamCount));
        _tasks = new ProducerSlot[streamCount];
    }

    internal void AdmitSupervision(SharpLinkClient owner, string operation)
    {
        if (_supervisionCompletion is not null) throw new InvalidOperationException("Group already admitted.");
        var completion = _supervisionCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { owner.AdmitStreamSupervision(completion.Task, operation, this, ref _supervisionRegistration); }
        catch (Exception failure)
        {
            // No origin/pending/head has started. An accepted registration owns a successful
            // empty anchor; only the distinct admission infrastructure failure belongs to caller.
            completion.TrySetResult();
            if (_supervisionRegistration is { } accepted)
            {
                try { owner.CompleteAcceptedStreamSupervision(accepted); }
                catch (Exception cleanup) { failure = AddPhysical(failure, cleanup)!; }
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    // Called before pending publication; transfers the original selection reservation exactly once.
    internal void PrepareRegistration(ClientConnection connection, RpcBindingPublication binding, long dispatcherLease = 0)
    {
        if (!ReferenceEquals(connection.Session, binding.Session) ||
            StreamCount > connection.Session.BindingAuthority.Limits.MaxClientStreamsPerCall)
            throw new InvalidOperationException("Invalid exact stream registration identity or count.");
        lock (_gate)
        {
            if (_registrationBinding is not null || _selectionOwned || HasProducerLifetime || _finished)
                throw new InvalidOperationException("Group registration was already prepared.");
            _connection = connection;
            _registrationBinding = binding;
            _responseDispatcherLease = dispatcherLease;
            _selectionOwned = true;
        }
    }

    internal void BeginPendingRegistration(PendingCallKind kind)
    {
        lock (_gate)
        {
            if (_registrationBinding is null || _registrationClaimed || HasProducerLifetime || _finished ||
                kind is not (PendingCallKind.ClientStreaming or PendingCallKind.OneWayClientStreaming or PendingCallKind.DuplexStreaming))
                throw new InvalidOperationException("Producing group registration must be prepared and attempted exactly once.");
            _registrationClaimed = true;
        }
    }

    // Registrar owns NEW call.CompletionGate across winning slot CAS. No callbacks/transport.
    internal void AdoptRegisteredCall(PendingRequestTable table, PendingRequestTable.PendingCall call, long id)
    {
        lock (_gate)
        {
            InitializeIdentity(_registrationBinding!, id);
            if (!call.TryAttachProducer(out var token))
                throw new InvalidOperationException("Fresh producing call could not adopt its original producer lease.");
            ProducerToken = token;
            _pendingLifetime = new PendingRequestTable.ProducerLifetimeLease(table, call, id);
            _registeredOperation = call.Operation;
            HasProducerLifetime = true;
            call.StreamGroup = this;
        }
    }

    internal RpcRequestOperation<T>? TakeRegisteredOperation<T>()
    {
        lock (_gate)
        {
            var operation = _registeredOperation as RpcRequestOperation<T>;
            _registeredOperation = null;
            return operation;
        }
    }

    // Definitive registration failure before publication, not merely a throwing OnRegistered.
    internal void MarkNoPending()
    {
        lock (_gate)
        {
            if (HasProducerLifetime) throw new InvalidOperationException("An adopted group still owns its pending terminal.");
            _logicalFinalized = true;
        }
        TryFinishSupervision();
    }

    // Incomplete head ValueTask is consumed exactly once as this Task. It roots the actual
    // startup await continuation until the final producer/cleanup Task replaces it after exit.
    internal void RetainHeadTask(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            if (_originAssigned || _originTask is not null)
                throw new InvalidOperationException("A group has only one startup head task.");
            _originTask = task;
        }
    }

    internal void AttachOriginTask(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            if (_originAssigned || _originTask is { IsCompleted: false })
                throw new InvalidOperationException("Origin assignment cannot overtake an unfinished head task or another origin.");
            _originTask = task;
            _originAssigned = true; // Strong assignment precedes any completion processing.
        }
        if (task.IsCompleted) OnOriginExited(task);
        else _ = task.ContinueWith(static (finished, state) => ((ClientStreamPublicationGroup)state!).OnOriginExited(finished),
            this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal void BeginResponseRoutePublication()
    {
        lock (_gate)
        {
            if ((_responseRoutePublication & ResponseRoutePhaseMask) != 0 || _logicalFinalized)
                throw new InvalidOperationException("Response route publication must be prepared before pending callbacks.");
            _responseRoutePublication = (_responseRoutePublication & ~ResponseRoutePhaseMask) | 1;
        }
    }

    internal void ClaimResponseRoutePublication()
    {
        lock (_gate)
        {
            if ((_responseRoutePublication & ResponseRoutePhaseMask) != 1)
                throw new InvalidOperationException("An exact response route has one publication attempt.");
            _responseRoutePublication = (_responseRoutePublication & ~ResponseRoutePhaseMask) | 2;
        }
    }

    internal Task WaitForResponseRoutePublication()
    {
        lock (_gate)
            return (_responseRoutePublication & ResponseRoutePhaseMask) is 1 or 2
                ? (_responseRouteSettled ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task
                : Task.CompletedTask;
    }

    internal void CompleteResponseRoutePublication()
    {
        TaskCompletionSource? ready;
        lock (_gate)
        {
            if ((_responseRoutePublication & ResponseRoutePhaseMask) is 0 or 3) return;
            _responseRoutePublication = (_responseRoutePublication & ~ResponseRoutePhaseMask) | 3;
            ready = _responseRouteSettled;
        }
        ready?.TrySetResult();
        TryFinishSupervision();
    }

    // No new retain or dispatcher root: origin and terminal already hold the exact object.
    // Successful new-call adoption transfers the ORIGINAL registration retention. The old
    // post-registration constructor has no such transfer; neither does mere preparation.
    internal long FinishOriginSinkUseForRetention() => FinishSinkUseForRetention(OriginSinkUseDone);
    internal long FinishPendingSinkUseForRetention() => FinishSinkUseForRetention(PendingSinkUseDone);

    private long FinishSinkUseForRetention(int completedUse)
    {
        lock (_gate)
        {
            if (!_registrationClaimed || !HasProducerLifetime || _responseDispatcherLease == 0) return 0;
            _responseRoutePublication |= completedUse;
            const int bothUses = OriginSinkUseDone | PendingSinkUseDone;
            if ((_responseRoutePublication & ResponseRoutePhaseMask) != 3 ||
                (_responseRoutePublication & bothUses) != bothUses ||
                (_responseRoutePublication & OriginalSinkRetentionClaimed) != 0) return 0;
            _responseRoutePublication |= OriginalSinkRetentionClaimed;
            // Keep immutable identity for already captured receipt/diagnostic readers. Claim
            // ownership, NOT identity, is consumed. Actual release/callback runs outside gate.
            return _responseDispatcherLease;
        }
    }

    internal void ClaimResponseDrain()
    {
        if (!HasSupervisionAnchor) return;
        lock (_gate)
        {
            if (_logicalFinalized || _responseDrainClaimed)
                throw new InvalidOperationException("Response cleanup cannot start after receipt closure or twice.");
            _responseDrainClaimed = true;
        }
    }

    internal void AttachResponseDrain(Task task)
    {
        lock (_gate)
        {
            if (!_responseDrainClaimed || _responseDrainAssigned)
                throw new InvalidOperationException("Response drain must have exactly one prior owner claim.");
            _responseDrainTask = task;
            _responseDrainAssigned = true;
        }
        if (task.IsCompleted) OnResponseDrainExited(task);
        else _ = task.ContinueWith(static (finished, state) => ((ClientStreamPublicationGroup)state!).OnResponseDrainExited(finished),
            this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static Exception? ReadPhysicalOutcome(Task task)
    {
        if (task.IsCanceled) return new InvalidOperationException("A selected physical task lost its raw cancellation envelope.");
        Exception? failure = null;
        if (task.Exception is { } container)
            foreach (var error in container.InnerExceptions)
                failure = AddPhysical(failure, PhysicalTaskFailureException.GetRaw(error));
        return failure;
    }

    private void OnOriginExited(Task task)
    {
        var failure = ReadPhysicalOutcome(task);
        lock (_gate)
        {
            if (_originExited) return;
            _supervisionFailure = AddPhysical(_supervisionFailure, failure);
            _originExited = true;
            _originTask = null; // Actual exit + raw observation complete; phase records prior start.
        }
        TryFinishSupervision();
    }

    private void OnResponseDrainExited(Task task)
    {
        var failure = ReadPhysicalOutcome(task);
        lock (_gate)
        {
            if (_responseDrainExited) return;
            _supervisionFailure = AddPhysical(_supervisionFailure, failure);
            _responseDrainExited = true;
            _responseDrainTask = null;
        }
        TryFinishSupervision();
    }

    private void FinalizeSupervisionReceipt()
    {
        lock (_gate) _logicalFinalized = true;
        TryFinishSupervision();
    }

    private void TryFinishSupervision()
    {
        TaskCompletionSource? completion;
        Exception? failure;
        lock (_gate)
        {
            if (_supervisionCompletion is null || _supervisionFinished || !_originAssigned || !_originExited || !_logicalFinalized ||
                (_responseRoutePublication & ResponseRoutePhaseMask) is 1 or 2 ||
                _responseDrainClaimed && (!_responseDrainAssigned || !_responseDrainExited)) return;
            _supervisionFinished = true;
            completion = _supervisionCompletion;
            failure = _supervisionFailure;
            _originTask = null;
            _responseDrainTask = null;
            _supervisionFailure = null;
            _registeredOperation = null;
        }
        if (failure is null) completion.TrySetResult();
        else completion.TrySetException(PhysicalTaskFailureException.From(failure));
    }
}
