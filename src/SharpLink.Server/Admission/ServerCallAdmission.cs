namespace SharpLink.Server;

internal enum ServerCallAdmissionResult : byte
{
    Acquired,
    Unavailable,
    PerConnectionCapacityExhausted,
    ServerCapacityExhausted
}

/// <summary>
/// Owns server-wide call-admission accounting and the local-to-global capacity transfer.
/// Server lifecycle and drain publication remain owned by <see cref="SharpLinkServer"/> and are
/// observed through direct calls on that sealed owner so extraction adds no delegate dispatch to
/// the request hot path.
/// </summary>
internal sealed class ServerCallAdmission
{
    private readonly SharpLinkServer _server;
    private ServerCallCapacityLimits _limits;
    private int _globalActiveCalls;
    private int _pendingCallAdmissions;

    internal ServerCallAdmission(
        SharpLinkServer server,
        int maxConcurrentCallsPerConnection,
        int maxConcurrentCallsPerServer)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
        _limits = ServerCallCapacityLimits.CreateValidated(
            maxConcurrentCallsPerConnection,
            maxConcurrentCallsPerServer);
    }

    internal int ActiveCallCount => Volatile.Read(ref _globalActiveCalls);

    internal int PendingCallAdmissions => Volatile.Read(ref _pendingCallAdmissions);

    internal int MaxConcurrentCallsPerConnection
        => Volatile.Read(ref _limits).MaxConcurrentCallsPerConnection;

    internal int MaxConcurrentCallsPerServer
        => Volatile.Read(ref _limits).MaxConcurrentCallsPerServer;

    internal ServerResourceGovernor ResourceGovernor => _server.ResourceGovernorForCallAdmission;

    internal void TrackPhysicalTail(Task task, ref FrameworkTaskRegistration? receipt)
        => _server.TrackStrictPhysicalRuntimeTask(task, "GeneratedPhysicalTail", ref receipt);

    internal void UpdateLimits(ServerCallCapacityLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Volatile.Write(ref _limits, limits);
    }

    internal ServerCallAdmissionResult TryAcquireCall(ServerConnectionState connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!_server.IsRunningForCallAdmission)
            return ServerCallAdmissionResult.Unavailable;

        Interlocked.Increment(ref _pendingCallAdmissions);
        try
        {
            // Stop can begin between the first Running check and the pending increment. Once this
            // check succeeds, the pending count covers every local -> global transfer and rollback.
            if (!_server.IsRunningForCallAdmission)
                return ServerCallAdmissionResult.Unavailable;

            // One immutable snapshot is the acquisition linearization point relative to capacity
            // updates. Both independent limits are therefore evaluated from the same generation.
            var limits = Volatile.Read(ref _limits);
            connection.DeadlineScheduler.EnsureMaxCalls(limits.MaxConcurrentCallsPerConnection);
            if (!connection.TryAcquireCall(limits.MaxConcurrentCallsPerConnection))
            {
                return connection.LifecycleState == ServerConnectionLifecycleState.Ready
                    ? ServerCallAdmissionResult.PerConnectionCapacityExhausted
                    : ServerCallAdmissionResult.Unavailable;
            }

#if DEBUG
            connection.NotifyAfterLocalCallAdmissionForTesting();
#endif

            if (!TryAcquireGlobalCall(limits.MaxConcurrentCallsPerServer))
            {
                // The provisional global increment remains owned until the paired local slot is
                // released so drain cannot observe zero global calls while local ownership remains.
                ReleaseCall(connection);
                return ServerCallAdmissionResult.ServerCapacityExhausted;
            }

            if (_server.IsRunningForCallAdmission)
                return ServerCallAdmissionResult.Acquired;

            ReleaseCall(connection);
            return ServerCallAdmissionResult.Unavailable;
        }
        finally
        {
            EndPendingCallAdmission(connection);
        }
    }

    internal ServerCallAdmissionResult TryReserveCall(
        ServerConnectionState connection,
        out ServerRequestPermit? permit)
        => TryReserveCall(connection, testHooks: null, out permit);

    internal ServerCallAdmissionResult TryReserveCall(
        ServerConnectionState connection,
        ServerRequestPermitTestHooks? testHooks,
        out ServerRequestPermit? permit)
        => TryReserveCall(connection, testHooks, mayDecode: true, out permit);

    internal ServerCallAdmissionResult TryReserveCall(
        ServerConnectionState connection,
        ServerRequestPermitTestHooks? testHooks,
        bool mayDecode,
        out ServerRequestPermit? permit)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var admission = TryAcquireCall(connection);
        if (admission != ServerCallAdmissionResult.Acquired)
        {
            permit = null;
            return admission;
        }

        try
        {
            permit = new ServerRequestPermit(this, connection, testHooks, mayDecode);
            return ServerCallAdmissionResult.Acquired;
        }
        catch
        {
            // Capacity is already owned when permit materialization begins. Roll both scopes back
            // synchronously if construction fails so no admitted slot can become orphaned.
            ReleaseCall(connection);
            throw;
        }
    }

    internal void ReleaseCall(ServerConnectionState connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Exception? failure = null;
        try { connection.ReleaseCall(); }
        catch (Exception exception) { failure = exception; }
        try { ReleaseGlobalCall(); }
        catch (Exception exception) { failure = CombineReleaseFailures(failure, exception); }
        try
        {
            if (!_server.IsRunningForCallAdmission)
                _server.TrySignalCallsDrainedForCallAdmission(connection);
        }
        catch (Exception exception) { failure = CombineReleaseFailures(failure, exception); }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Exception CombineReleaseFailures(Exception? first, Exception next)
        => first is null ? next : new AggregateException(first, next);

    private bool TryAcquireGlobalCall(int maxConcurrentCallsPerServer)
    {
        if (Interlocked.Increment(ref _globalActiveCalls) <= maxConcurrentCallsPerServer)
            return true;

        // The caller owns both provisional slots at this point. It must release the connection slot
        // before decrementing this global slot so server drain cannot become visible between them.
        return false;
    }

    private void ReleaseGlobalCall()
    {
        var active = Interlocked.Decrement(ref _globalActiveCalls);
        if (active < 0)
            throw new InvalidOperationException("Server global active call count underflowed.");
    }

    private void EndPendingCallAdmission(ServerConnectionState connection)
    {
        var remaining = Interlocked.Decrement(ref _pendingCallAdmissions);
        if (remaining < 0)
            throw new InvalidOperationException("Server pending call admission count underflowed.");
        if (remaining == 0 && !_server.IsRunningForCallAdmission)
            _server.TrySignalCallsDrainedForCallAdmission(connection);
    }
}

/// <summary>
/// Unique owner for one accepted request's call capacity and optional decode resources.
/// A Reserved permit already owns local/global call capacity; activation is only a lifecycle phase
/// transition, preserving the existing drain-safe accounting until the two-phase lifecycle changes.
/// </summary>
internal sealed class ServerRequestPermit : IDisposable, IGeneratedStreamPhysicalOwner
{
    private const int Reserved = 0;
    private const int Activating = 1;
    private const int Active = 2;
    private const int Releasing = 3;
    private const int Disposed = 4;

    private readonly ServerCallAdmission _admission;
    private readonly ServerConnectionState _connection;
    private readonly ServerRequestPermitTestHooks? _testHooks;
    private readonly Lock? _resourceGate;
    private ServerDecodePermit? _decodePermit;
    private int _state = Reserved;

    internal ServerRequestPermit(
        ServerCallAdmission admission,
        ServerConnectionState connection,
        ServerRequestPermitTestHooks? testHooks,
        bool mayDecode = true)
    {
        // The wire compression flag fixes resource capability before publication. A
        // plain request cannot later attach decode resources or upgrade this mode.
        _resourceGate = mayDecode ? new Lock() : null;
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _testHooks = testHooks;
    }

    private ServiceRegistration? _protectedRegistration;
    private SharpLinkDynamicModuleLease _moduleLease;

    internal bool TryProtectRegistration(ServiceRegistration registration, RpcMethodFacts method)
    {
        if (!IsReserved || _protectedRegistration is not null)
            throw new InvalidOperationException("Invocation ownership is acquired once after the request permit.");
        if (!registration.TryAcquireInvocationOwnership(method, out var lease))
            return false;
        _moduleLease = lease;
        _protectedRegistration = registration;
        return true;
    }

    internal void VerifyProtectedRegistration(ServiceRegistration registration)
    {
        if (!ReferenceEquals(_protectedRegistration, registration) ||
            registration.HasModule && !_moduleLease.IsAcquired ||
            Volatile.Read(ref _state) is Releasing or Disposed)
            throw new InvalidOperationException("Activation requires the exact pre-decode invocation owner.");
    }

    // One existing request owner records transfer of its exact inbound child-cleanup handle.
    // This is not a counter: later outer error paths must not independently abandon its routes.
    private int _ownsInboundStreamCleanup;
    internal bool OwnsInboundStreamCleanup => Volatile.Read(ref _ownsInboundStreamCleanup) != 0;
    internal void MarkInboundStreamCleanupOwned()
    {
        if (Interlocked.Exchange(ref _ownsInboundStreamCleanup, 1) != 0)
            throw new InvalidOperationException("Inbound stream cleanup was transferred twice.");
    }

    private int _physicalTails;
    private int _disposeAfterPhysicalTails;
    private TaskCompletionSource? _physicalTailsCompleted;
    private Task? _physicalServiceCleanup;
    private ServerCallCancellationState? _physicalCallState;
    private ServerCallCancellationLease? _capturedPhysicalCallState;
    private Lock? _physicalTailGate;
    private int _physicalCallStateLost;
    private const int PhysicalEverRetained = 1;
    private const int PhysicalRegistrationSealed = 2;
    private const int PhysicalServiceCleanupOwned = 4;
    private const int PhysicalServiceCleanupFinished = 8;
    private int _physicalOutcomeFlags;
    // Fixed stage domain, allocated only on an actual physical failure. No per-item log.
    private Exception?[]? _physicalFailures;

    internal void RememberPhysicalCallState(in ServerCallCancellationLease lease)
        => _capturedPhysicalCallState = lease;

    internal void PreserveCallStateForPhysicalCompletion(ServerCallCancellationState state, long requestId)
    {
        if (!HasPendingPhysicalOwnership || Volatile.Read(ref _physicalCallState) is not null ||
            Volatile.Read(ref _physicalCallStateLost) != 0)
            return;
        var lease = state.CaptureLease(requestId);
        if (!lease.TryAcquire())
            throw new InvalidOperationException("Physical producer lost its exact call-state generation.");
        if (Interlocked.CompareExchange(ref _physicalCallState, state, null) is not null)
            lease.ReleaseUse();
    }

    internal bool HasPhysicalTails => Volatile.Read(ref _physicalTails) != 0;

    // Must be called by the invocation owner before starting its cleanup task. A tail that
    // already completed still owns its error, and cannot evade this branch via count == 0.
    internal bool BeginPhysicalServiceCleanup()
    {
        // Even the no-tail path seals BEFORE observing the lazily published gate.
        // A first Retain either atomically claims the old unsealed phase under its gate,
        // or loses to this seal and cannot create an owner after the invocation returned.
        Interlocked.Or(ref _physicalOutcomeFlags, PhysicalRegistrationSealed);
        var gate = Volatile.Read(ref _physicalTailGate);
        if (gate is null) return false;
        lock (gate)
        {
            if ((_physicalOutcomeFlags & PhysicalServiceCleanupOwned) != 0 ||
                Volatile.Read(ref _state) is Releasing or Disposed)
                throw new InvalidOperationException("Physical service cleanup is transferred once before release.");
            if ((_physicalOutcomeFlags & PhysicalEverRetained) == 0 && _physicalFailures is null)
                return false;
            Interlocked.Or(ref _physicalOutcomeFlags, PhysicalServiceCleanupOwned);
            return true;
        }
    }

    internal void AttachPhysicalServiceCleanup(Task cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (Interlocked.CompareExchange(ref _physicalServiceCleanup, cleanup, null) is not null)
            throw new InvalidOperationException("Physical service cleanup is transferred once.");
        // No discarded release continuation. The supervised task calls Finish below itself.
    }

    internal void FinishPhysicalServiceCleanup()
    {
        var gate = Volatile.Read(ref _physicalTailGate)
            ?? throw new InvalidOperationException("Physical cleanup has no owner gate.");
        bool release;
        lock (gate)
        {
            if ((_physicalOutcomeFlags & PhysicalServiceCleanupOwned) == 0 ||
                (_physicalOutcomeFlags & PhysicalServiceCleanupFinished) != 0)
                throw new InvalidOperationException("Physical service cleanup finished without its unique ownership.");
            Interlocked.Or(ref _physicalOutcomeFlags, PhysicalServiceCleanupFinished);
            release = Volatile.Read(ref _disposeAfterPhysicalTails) != 0;
        }
        // Mark finished before release: Dispose must not await the current cleanup task.
        // A release failure belongs to this task, not an unobserved continuation.
        if (release) Dispose();
    }

    private bool HasPendingPhysicalOwnership
    {
        get
        {
            var flags = Volatile.Read(ref _physicalOutcomeFlags);
            // Sealing only forbids new producers. It NEVER means completed producer errors
            // or service cleanup have been consumed. In particular, Begin may be paused after
            // its atomic seal but before taking the gate/claiming ServiceCleanupOwned.
            return HasPhysicalTails ||
                (flags & PhysicalServiceCleanupFinished) == 0 &&
                ((flags & (PhysicalEverRetained | PhysicalServiceCleanupOwned)) != 0 ||
                 Volatile.Read(ref _physicalFailures) is not null);
        }
    }

    private Lock GetPhysicalTailGate()
        => Volatile.Read(ref _physicalTailGate) ??
            Interlocked.CompareExchange(ref _physicalTailGate, new Lock(), null) ?? _physicalTailGate!;

    public void TrackPhysicalTail(Task task, ref FrameworkTaskRegistration? registration)
        => _admission.TrackPhysicalTail(task, ref registration);

    public void ReportPhysicalFailure(
        GeneratedPhysicalFailureStage stage, Exception failure, ref bool accepted)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var index = (int)stage;
        if ((uint)index >= 3)
            throw new ArgumentOutOfRangeException(nameof(stage));
        lock (GetPhysicalTailGate())
        {
            if ((_physicalOutcomeFlags & PhysicalServiceCleanupFinished) != 0 ||
                Volatile.Read(ref _state) is Releasing or Disposed)
                throw new InvalidOperationException("Physical failure arrived after cleanup ownership ended.");
            var failures = _physicalFailures ??= new Exception?[8];
            if (failures[index] is not null)
            {
                failures[7] ??= new InvalidOperationException("Physical failure stage was reported more than once.");
                throw new InvalidOperationException("Physical failure stage has a prior owner.");
            }
            failures[index] = failure;
            accepted = true;
        }
    }

    public void RetainPhysicalTail()
    {
        var gate = GetPhysicalTailGate();
        SharpLinkProtocolViolationException? lostGeneration = null;
        lock (gate)
        {
            if (Volatile.Read(ref _state) is Releasing or Disposed ||
                (_physicalOutcomeFlags & PhysicalRegistrationSealed) != 0)
                throw new InvalidOperationException("A detached producer cannot borrow a released or sealed request owner.");
            var flags = Volatile.Read(ref _physicalOutcomeFlags);
            if ((flags & PhysicalRegistrationSealed) != 0 ||
                Interlocked.CompareExchange(ref _physicalOutcomeFlags, flags | PhysicalEverRetained, flags) != flags)
                throw new InvalidOperationException("Physical registration lost the invocation seal race.");
            if (_physicalCallState is null && _capturedPhysicalCallState is { } captured)
            {
                if (captured.TryAcquire()) _physicalCallState = captured.State;
                else lostGeneration = new SharpLinkProtocolViolationException(ProtocolViolationReason.InternalState,
                    "Generated physical ownership lost its original call-state generation.");
            }
            Interlocked.Increment(ref _physicalTails);
            if (lostGeneration is not null)
                (_physicalFailures ??= new Exception?[8])[3] = lostGeneration;
        }
        if (lostGeneration is not null)
        {
            Volatile.Write(ref _physicalCallStateLost, 1);
            try { _connection.Session.NotifyDisconnected(lostGeneration); }
            catch (Exception failure)
            {
                lock (gate) (_physicalFailures ??= new Exception?[8])[4] = failure;
            }
        }
    }

    public void ReleasePhysicalTail()
    {
        TaskCompletionSource? completion;
        Exception? failure;
        lock (GetPhysicalTailGate())
        {
            if (_physicalTails <= 0)
                throw new InvalidOperationException("Generated physical tail released more than once.");
            var remaining = Interlocked.Decrement(ref _physicalTails);
            if (remaining != 0) return;
            completion = _physicalTailsCompleted;
            failure = GetPhysicalFailureLocked();
        }
        if (completion is not null)
        {
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
        // The invocation's service-cleanup owner performs final permit release. Tail exit
        // cannot dispose service/module ownership or race a not-yet-published cleanup task.
    }

    internal ValueTask JoinPhysicalTailsAsync()
    {
        var gate = Volatile.Read(ref _physicalTailGate);
        if (gate is null) return ValueTask.CompletedTask;
        lock (gate)
        {
            if (_physicalTails == 0)
            {
                var failure = GetPhysicalFailureLocked();
                return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
            }
            _physicalTailsCompleted ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(_physicalTailsCompleted.Task);
        }
    }

    private Exception? GetPhysicalFailureLocked()
    {
        Exception? failure = null;
        if (_physicalFailures is null) return null;
        foreach (var item in _physicalFailures)
            if (item is not null) failure = CombineReleaseFailures(failure, item);
        return failure;
    }

    private static Exception CombineReleaseFailures(Exception? first, Exception next)
        => first is null ? next : new AggregateException(first, next);

    internal bool IsReserved => Volatile.Read(ref _state) == Reserved;

    internal bool IsActive => Volatile.Read(ref _state) == Active;

    internal bool TryAcquireDecodePermit(
        long retainedCompressedBytes,
        out ServerDecodePermit? decodePermit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retainedCompressedBytes);
        if (_resourceGate is null)
        {
            decodePermit = null;
            return false;
        }

        lock (_resourceGate)
        {
            if (Volatile.Read(ref _state) != Reserved || _decodePermit is not null)
            {
                decodePermit = null;
                return false;
            }

            if (!_admission.ResourceGovernor.TryAcquireDecode(retainedCompressedBytes, out decodePermit))
                return false;

            _decodePermit = decodePermit;
            return true;
        }
    }

    internal bool TryAcquireDecodePermit(
        ServerRetainedCompressedPermit retainedPermit,
        out ServerDecodePermit? decodePermit)
    {
        ArgumentNullException.ThrowIfNull(retainedPermit);
        if (_resourceGate is null)
        {
            decodePermit = null;
            return false;
        }

        lock (_resourceGate)
        {
            if (Volatile.Read(ref _state) != Reserved || _decodePermit is not null)
            {
                decodePermit = null;
                return false;
            }

            if (!_admission.ResourceGovernor.TryAcquireDecode(retainedPermit, out decodePermit))
                return false;

            _decodePermit = decodePermit;
            return true;
        }
    }

    internal void ReleaseDecodeResources()
    {
        if (_resourceGate is null)
        {
            if (Volatile.Read(ref _state) is Activating or Active)
                throw new InvalidOperationException("Decode resources cannot be detached after call activation.");
            return;
        }
        ServerDecodePermit? decodePermit;
        lock (_resourceGate)
        {
            var current = Volatile.Read(ref _state);
            if (current is Activating or Active)
            {
                throw new InvalidOperationException(
                    "Decode resources cannot be detached after call activation.");
            }
            if (current is Releasing or Disposed)
                return;

            decodePermit = _decodePermit;
            _decodePermit = null;
        }

        decodePermit?.Dispose();
    }

    internal void TransferDecodedBytesTo(ServerCallCancellationState callState)
    {
        ArgumentNullException.ThrowIfNull(callState);
        if (_resourceGate is null)
            return;

        ServerDecodedBytesPermit? decodedBytesPermit;
        lock (_resourceGate)
        {
            var decodePermit = _decodePermit;
            if (decodePermit is null)
                return;
            decodedBytesPermit = decodePermit.DetachDecodedBytesOwnership();
        }

        if (decodedBytesPermit is null)
            return;

        try
        {
            callState.AttachDecodedBytesPermit(decodedBytesPermit);
        }
        catch
        {
            decodedBytesPermit.Dispose();
            throw;
        }
    }

    internal void Activate()
    {
        if (_resourceGate is null)
        {
            var previous = Interlocked.CompareExchange(ref _state, Active, Reserved);
            if (previous is Releasing or Disposed)
                throw new ObjectDisposedException(nameof(ServerRequestPermit));
            if (previous != Reserved)
                throw new InvalidOperationException("Only a reserved call permit can be activated.");
            return;
        }
        lock (_resourceGate)
        {
            var current = Volatile.Read(ref _state);
            if (current is Releasing or Disposed)
                throw new ObjectDisposedException(nameof(ServerRequestPermit));
            if (current != Reserved)
                throw new InvalidOperationException("Only a reserved call permit can be activated.");
            if (_decodePermit is not null && !_decodePermit.IsDecodeCompleted)
            {
                throw new InvalidOperationException(
                    "A request with decode resources cannot be activated before decode completes.");
            }

            var observed = Interlocked.CompareExchange(ref _state, Activating, Reserved);
            if (observed != Reserved)
            {
                if (observed is Releasing or Disposed)
                    throw new ObjectDisposedException(nameof(ServerRequestPermit));
                throw new InvalidOperationException("Only a reserved call permit can be activated.");
            }

            Volatile.Write(ref _state, Active);
        }
    }

    public void Dispose()
    {
        // Logical completion cannot free module/call capacity while Runtime owns an abandoned
        // producer. The generated bridge also joins before service/scope disposal.
        if (HasPendingPhysicalOwnership)
        {
            Volatile.Write(ref _disposeAfterPhysicalTails, 1);
            if (HasPendingPhysicalOwnership)
                return;
        }

        var spinner = new SpinWait();
        while (true)
        {
            if (HasPendingPhysicalOwnership)
            {
                Volatile.Write(ref _disposeAfterPhysicalTails, 1);
                if (HasPendingPhysicalOwnership) return;
            }
            var observed = Volatile.Read(ref _state);
            switch (observed)
            {
                case Reserved:
                    if (!TryClaimRelease(Reserved))
                        continue;
                    ReleaseBackingCapacity();
                    return;
                case Activating:
                    spinner.SpinOnce();
                    continue;
                case Active:
                    if (!TryClaimRelease(Active))
                        continue;
                    ReleaseBackingCapacity();
                    return;
                case Releasing:
                    _testHooks?.DisposeObservedReleasing?.Invoke();
                    spinner.SpinOnce();
                    continue;
                case Disposed:
                    return;
                default:
                    throw new InvalidOperationException("Unknown server request permit state.");
            }
        }
    }

    private bool TryClaimRelease(int expectedState)
    {
        bool claimed;
        if (_resourceGate is null)
            claimed = Interlocked.CompareExchange(ref _state, Releasing, expectedState) == expectedState;
        else
        {
            lock (_resourceGate)
                claimed = Interlocked.CompareExchange(ref _state, Releasing, expectedState) == expectedState;
        }
        if (!claimed) return false;
        // A first detach may have published its gate after Dispose's initial null read.
        // Retain checks Releasing under this gate; either it refuses, or its already-owned
        // count/phase makes this provisional claim roll back before backing resources move.
        var physicalGate = Volatile.Read(ref _physicalTailGate);
        if (physicalGate is not null)
        {
            lock (physicalGate)
            {
                if (HasPendingPhysicalOwnership)
                {
                    Volatile.Write(ref _disposeAfterPhysicalTails, 1);
                    Volatile.Write(ref _state, expectedState);
                    return false;
                }
            }
        }
        return true;
    }

    private void ReleaseBackingCapacity()
    {
        Exception? failure = null;
        try
        {
            try { _testHooks?.ReleaseClaimed?.Invoke(); }
            catch (Exception exception) { failure = exception; }
            ServerDecodePermit? decodePermit = null;
            try
            {
                if (_resourceGate is { } resourceGate)
                {
                    lock (resourceGate)
                    {
                        decodePermit = _decodePermit;
                        _decodePermit = null;
                    }
                }
            }
            catch (Exception exception) { failure = CombineReleaseFailures(failure, exception); }
            try { decodePermit?.Dispose(); }
            catch (Exception exception) { failure = CombineReleaseFailures(failure, exception); }
            try { Interlocked.Exchange(ref _physicalCallState, null)?.ReleaseUse(); }
            catch (Exception exception) { failure = CombineReleaseFailures(failure, exception); }
            _capturedPhysicalCallState = null;
            try { _moduleLease.Dispose(); }
            catch (Exception exception) { failure = CombineReleaseFailures(failure, exception); }
            _moduleLease = default;
            _protectedRegistration = null;
            try { _admission.ReleaseCall(_connection); }
            catch (Exception exception) { failure = CombineReleaseFailures(failure, exception); }
        }
        finally { Volatile.Write(ref _state, Disposed); }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

}

internal sealed class ServerRequestPermitTestHooks
{
    internal Action? ReleaseClaimed { get; init; }

    internal Action? DisposeObservedReleasing { get; init; }
}
