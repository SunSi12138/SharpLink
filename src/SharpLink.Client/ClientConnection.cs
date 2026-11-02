namespace SharpLink.Client;

internal enum ClientConnectionState : byte
{
    Ready,
    Draining,
    Closed
}

/// <summary>Owns all mutable call state associated with one physical RPC session.</summary>
internal sealed class ClientConnection :
    IPendingCallOwner,
    IStreamConsumerDeliveryGate,
    IAsyncDisposable
{
    // Only fatal publication and planned replacement take this gate. Ordinary RPC admission
    // remains lock-free and observes the shared redirect publication that performs the cut.
    // Never acquire a topology gate or run connection cleanup while holding this gate.
    private readonly Lock _sessionRefreshCommitGate = new();
    private bool _sessionRefreshCommitCompleted;

    private readonly SharpLinkClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cancellation;
    private readonly Func<long, IStreamDispatchState?, ValueTask> _consumerAbandonedCallback;
    private LateResponseLogLimiter _lateResponseLogLimiter;
    private int _state = (int)ClientConnectionState.Ready;
    private int _auxiliaryActiveCallCount;
    private int _callAdmissionReservations;
    private int _fatalFailureObservedForAdmission;
    private int _plannedSessionRefreshRetirement;
    private SessionRefreshRedirect? _sessionRefreshRedirect;
    private int _disposed;
    private int _localAdmissionDrainReasons; // bit0: identity exhaustion; bit1: peer binding status12

    // Assigned by the physical topology owner; moved only into that owner's real cleanup.
    // One effective admission cut and one detach, never an append-only route history.
    private bool _contractRouteDrainPublished;
    private SharpLinkClient.ContractRouteFailure? _contractRouteCutFailure;
    private SharpLinkClient.ContractRouteFailure? _contractRouteDetachFailure;
    internal bool TryBeginContractRouteDrain()
    {
        if (_contractRouteDrainPublished) return false;
        _contractRouteDrainPublished = true;
        return true;
    }
    internal void RetainContractRouteCutFailure(SharpLinkClient.ContractRouteFailure? failure)
        => _contractRouteCutFailure = failure;
    internal void RetainContractRouteDetachFailure(SharpLinkClient.ContractRouteFailure? failure)
        => _contractRouteDetachFailure = failure;
    internal SharpLinkClient.ContractRouteFailure? TakeContractRouteFailures()
    {
        var cut = Interlocked.Exchange(ref _contractRouteCutFailure, null);
        var detach = Interlocked.Exchange(ref _contractRouteDetachFailure, null);
        if (cut is null) return detach;
        if (detach is not null) cut.Add("physical-detach", detach);
        return cut;
    }

    public ClientConnection(
        SharpLinkClient client,
        RpcSession session,
        CancellationTokenSource cancellation,
        int maxPendingCalls,
        SharpLinkRuntimeContext runtimeContext,
        string? endpointId = null,
        long endpointGeneration = 0)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        ArgumentNullException.ThrowIfNull(runtimeContext);
        _timeProvider = runtimeContext.TimeProvider;
        _lateResponseLogLimiter = new LateResponseLogLimiter(_timeProvider.TimestampFrequency);
        _consumerAbandonedCallback = OnConsumerAbandonedAsync;
        Binding = new ClientBindingSession(session, client.BindingBudget, OnProtocolIdentityExhausted,
            controlOwnerReleased: () => _client.NotifyBindingControlOwnerReleased(this));
        try
        {
            PendingCalls = new PendingRequestTable(
                maxPendingCalls,
                runtimeContext.Codecs,
                this,
                runtimeContext.TimeProvider);
        }
        catch (Exception exception)
        {
            Binding.Close(exception);
            throw;
        }
        EndpointId = endpointId;
        EndpointGeneration = endpointGeneration;
    }

    internal ClientBindingSession Binding { get; }
    internal bool TryReserveExactCallAdmission() => TryReserveOwnCallAdmission(notifyTestHook: true);

    public RpcSession Session { get; }
    public PendingRequestTable PendingCalls { get; }
    public string? EndpointId { get; }
    public long EndpointGeneration { get; }

    public ClientConnectionState State
        => (ClientConnectionState)Volatile.Read(ref _state);

    public bool CanAcceptCalls
        => IsOwnCallAdmissionOpen &&
           State == ClientConnectionState.Ready &&
           Session.CanAcceptCalls;

    // A successful selection already owns this physical connection. A planned refresh may
    // close NEW admission without invalidating that reservation; fatal/drain/session gates remain.
    internal bool CanHonorReservedCallAdmission
        => Volatile.Read(ref _fatalFailureObservedForAdmission) == 0 &&
           !RequiresCapacitySafeDrain &&
           State == ClientConnectionState.Ready &&
           Session.CanAcceptCalls;

    public int ActiveCallCount
        => PendingCalls.ActiveCount + Volatile.Read(ref _auxiliaryActiveCallCount);

    internal int CallAdmissionReservationCount => Volatile.Read(ref _callAdmissionReservations);

    // Local exhaustion/status12 is not spare retiring capacity: the physical connection stays charged
    // against the ordinary pool until its admitted owners leave and it is detached for cleanup.
    internal bool HasExhaustedProtocolIdentity => (Volatile.Read(ref _localAdmissionDrainReasons) & 1) != 0 ||
        PendingCalls.RequestIdsExhausted;
    internal bool RequiresCapacitySafeDrain => Volatile.Read(ref _localAdmissionDrainReasons) != 0 ||
        PendingCalls.RequestIdsExhausted;

    internal bool OccupiesConnectionCapacity => State == ClientConnectionState.Ready ||
        State == ClientConnectionState.Draining && RequiresCapacitySafeDrain;

    internal void OnProtocolIdentityExhausted()
    {
        if ((Interlocked.Or(ref _localAdmissionDrainReasons, 1) & 1) == 0)
        {
            Binding.SealAdmission();
            _client.MarkProtocolIdentityExhaustionDraining(this);
        }
    }

    internal void OnBindingSessionDraining()
    {
        if ((Interlocked.Or(ref _localAdmissionDrainReasons, 2) & 2) == 0)
        {
            Binding.SealAdmission();
            _client.MarkBindingSessionDraining(this);
        }
    }

    internal bool HasObservedFatalFailureForAdmission
        => Volatile.Read(ref _fatalFailureObservedForAdmission) != 0;

    internal bool HasPlannedSessionRefreshRetirement
        => Volatile.Read(ref _plannedSessionRefreshRetirement) != 0;

    /// <summary>
    /// The shared redirect that retires this connection's refresh lineage into the newest Ready
    /// replacement. <see langword="null"/> until the connection takes part in a refresh cut.
    /// </summary>
    internal SessionRefreshRedirect? SessionRefreshRedirect
        => Volatile.Read(ref _sessionRefreshRedirect);

    internal bool TryReserveCallAdmission(out ClientConnection admitted)
    {
        if (TryReserveOwnCallAdmission(notifyTestHook: true))
        {
            admitted = this;
            return true;
        }

        var redirect = Volatile.Read(ref _sessionRefreshRedirect);
        if (redirect is null)
        {
            admitted = null!;
            return false;
        }

        // The redirect always targets the newest Ready connection in the lineage, so this is a
        // constant-depth lookup rather than a walk over retired generations. The retry only
        // re-reads the same indirection when a concurrent cut published a newer target between
        // the read and the reservation attempt.
        var candidate = redirect.Current;
        for (var attempt = 0; candidate is not null && attempt < 3; attempt++)
        {
            if (candidate.TryReserveOwnCallAdmission(notifyTestHook: true))
            {
                admitted = candidate;
                return true;
            }
            var next = redirect.Current;
            if (ReferenceEquals(next, candidate))
                break;
            candidate = next;
        }

        admitted = null!;
        return false;
    }

    internal bool TryReserveSessionRefreshCommit()
    {
        _client.NotifyBeforeSessionRefreshEligibilityCommitForTest(this);
        if (!TryReserveOwnCallAdmission(notifyTestHook: false))
            return false;

        // The reservation only proves the replacement was admission-eligible at this instant.
        // The source cut occurs later when TryCommitSessionRefreshRetirement publishes the
        // shared redirect. This hook freezes the window in between for review regressions.
        _client.NotifyAfterSessionRefreshCommitReservationForTest(this);
        return true;
    }

    /// <summary>
    /// Commits the source-to-replacement cut while the caller holds the topology gate and an
    /// admission reservation on this replacement. A rejected attempt leaves the source unchanged.
    /// </summary>
    internal bool TryCommitSessionRefreshRetirement(ClientConnection source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this))
            throw new ArgumentException("A session-refresh source cannot replace itself.", nameof(source));

        _client.NotifyBeforeSessionRefreshCutLockForTest(this);
        lock (_sessionRefreshCommitGate)
        {
            if (_sessionRefreshCommitCompleted || !CanAcceptCalls || !source.CanAcceptCalls)
                return false;

            var redirect = source.GetOrCreateSessionRefreshRedirect();
            Volatile.Write(ref _sessionRefreshRedirect, redirect);
            _client.NotifyBeforeSessionRefreshCutPublicationForTest(source, this);

            // This single publication is the eligibility cut for ordinary RPC admission on
            // BOTH connections, including readers of older snapshots in the same lineage.
            // The replacement gate excludes fatal publication through this point: failure
            // published first rejects the attempt above; failure published later is post-cut.
            redirect.Publish(this);
            _client.NotifyAfterSessionRefreshCutPublicationForTest(source, this);

            // Retirement bookkeeping may lag the cut; admission already follows the redirect.
            Volatile.Write(ref source._plannedSessionRefreshRetirement, 1);
            _sessionRefreshCommitCompleted = true;
            return true;
        }
    }

    internal void ObserveFatalFailureForAdmission()
    {
        _client.NotifyBeforeFatalFailurePublicationForTest(this);
        lock (_sessionRefreshCommitGate)
        {
            if (_fatalFailureObservedForAdmission != 0)
                return;
            Volatile.Write(ref _fatalFailureObservedForAdmission, 1);
            _client.NotifyAfterFatalFailurePublicationForTest(this);
        }
        // The caller may acquire its topology gate or start cleanup only after releasing this
        // gate, so a refresh holding the topology gate cannot deadlock with fatal publication.
    }

    private bool IsOwnCallAdmissionOpen
    {
        get
        {
            if (Volatile.Read(ref _fatalFailureObservedForAdmission) != 0 || RequiresCapacitySafeDrain)
                return false;

            var redirect = Volatile.Read(ref _sessionRefreshRedirect);
            return redirect is null || ReferenceEquals(redirect.Current, this);
        }
    }

    private bool TryReserveOwnCallAdmission(bool notifyTestHook)
    {
        if (!IsOwnCallAdmissionOpen ||
            State != ClientConnectionState.Ready || !Session.CanAcceptCalls)
        {
            return false;
        }

        Interlocked.Increment(ref _callAdmissionReservations);
        if (!IsOwnCallAdmissionOpen ||
            State != ClientConnectionState.Ready || !Session.CanAcceptCalls)
        {
            ReleaseCallAdmissionReservation();
            return false;
        }

        if (notifyTestHook)
            _client.NotifyCallAdmissionReservedForTest(this);
        return true;
    }

    /// <summary>
    /// Releases one original selection reservation if present. Non-producing invocation sites
    /// transfer it after successful pending registration; outgoing producer groups retain it
    /// through every physical producer and their final fence. Direct table rentals own none.
    /// </summary>
    internal void ReleaseCallAdmissionReservation()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _callAdmissionReservations);
            if (observed == 0)
                return;
            if (Interlocked.CompareExchange(
                    ref _callAdmissionReservations,
                    observed - 1,
                    observed) != observed)
            {
                continue;
            }
            if (observed == 1)
            {
                _client.TryAdvancePlannedSessionRefreshRetirement(this);
                if (RequiresCapacitySafeDrain)
                    _client.RetireDrainingConnectionIfIdle(this);
            }
            return;
        }
    }

    private SessionRefreshRedirect GetOrCreateSessionRefreshRedirect()
    {
        var existing = Volatile.Read(ref _sessionRefreshRedirect);
        if (existing is not null)
            return existing;

        var created = new SessionRefreshRedirect(this);
        return Interlocked.CompareExchange(ref _sessionRefreshRedirect, created, null) ?? created;
    }

    internal void CompletePlannedSessionRefreshRetirement()
        => Volatile.Write(ref _plannedSessionRefreshRetirement, 0);

    internal void AssertStateInvariant()
    {
        var activeCalls = ActiveCallCount;
        if (activeCalls < 0)
            throw new InvalidOperationException("Client connection active call count became negative.");

        var state = State;
        var sessionAcceptsCalls = Session.CanAcceptCalls;
        if (state == ClientConnectionState.Ready && !sessionAcceptsCalls)
        {
            throw new InvalidOperationException(
                "A Ready client connection must reference a Session that accepts new calls at a stable lifecycle boundary.");
        }
        if (state == ClientConnectionState.Draining && sessionAcceptsCalls && !RequiresCapacitySafeDrain)
        {
            throw new InvalidOperationException(
                "A Draining client connection must not reference a Session that accepts new calls at a stable lifecycle boundary.");
        }
    }

    public CancellationToken CancellationToken => _cancellation.Token;

    public Func<long, IStreamDispatchState?, ValueTask> ConsumerAbandonedCallback
        => _consumerAbandonedCallback;

    bool IStreamConsumerDeliveryGate.TryAcceptStreamDelivery(long requestId)
        => PendingCalls.TryAcceptStreamData(requestId);

    internal bool ShouldLogLateResponse(out int suppressedCount)
        => _lateResponseLogLimiter.ShouldLog(_timeProvider.GetTimestamp(), out suppressedCount);

    public bool MarkDraining() => MarkDrainingCore(drainPhysicalSession: true);

    // Local exhaustion/status12 withdraws owner admission, not publication rights of reservations
    // already issued before the cut. Their final Request/Bind must still enqueue in Ready.
    internal bool MarkDrainingPreservingAdmittedPublication() => MarkDrainingCore(drainPhysicalSession: false);

    private bool MarkDrainingCore(bool drainPhysicalSession)
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)ClientConnectionState.Draining,
                (int)ClientConnectionState.Ready) == (int)ClientConnectionState.Ready)
        {
            if (drainPhysicalSession)
                Session.MarkDraining();
            SharpLinkTelemetry.AddClientRetiringConnections(1);
            return true;
        }

        return false;
    }

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ObserveFatalFailureForAdmission();
        var previousState = Interlocked.Exchange(ref _state, (int)ClientConnectionState.Closed);
        if (previousState == (int)ClientConnectionState.Closed)
            return;
        if (previousState == (int)ClientConnectionState.Draining)
            SharpLinkTelemetry.AddClientRetiringConnections(-1);

        Exception? cancellationException = null;
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception callbackException)
        {
            cancellationException = callbackException;
        }
        PendingCalls.FailAllPendingRequests(exception);
        Session.StreamManager.CompleteAll(exception);
        if (cancellationException is not null)
            _client.ReportConnectionCancellationCallbackFailure(cancellationException);
    }

    public bool TryBeginUntrackedCall()
    {
        Interlocked.Increment(ref _auxiliaryActiveCallCount);
        ReleaseCallAdmissionReservation();
        // A pre-cut reservation may start on a planned source, but never after a fatal
        // observation even while physical teardown is waiting for the topology gate.
        if (!HasObservedFatalFailureForAdmission &&
            (State == ClientConnectionState.Ready ||
             State == ClientConnectionState.Draining && RequiresCapacitySafeDrain && Session.CanAcceptCalls) &&
            Session.IsConnected)
            return true;

        ReleaseAuxiliaryActiveCall();
        return false;
    }

    public void EndUntrackedCall() => ReleaseAuxiliaryActiveCall();

    internal async Task SendClientStreamCoreAsync<T>(
        ClientStreamPublicationGroup group, ushort streamId, IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec, CancellationToken cancellationToken)
    {
        var requestId = group.RequestId;
        RpcDeadline deadline = default;
        IAsyncEnumerator<T>? enumerator = null;
        Exception? business = null;
        Exception? independent = null;
        var publishingTerminal = false;
        try
        {
            group.ValidateProducer(Session, requestId);
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(codec);
            cancellationToken.ThrowIfCancellationRequested();
            if (!PendingCalls.TryGetProducerDeadline(requestId, out deadline))
                throw new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "The owning RPC call is no longer active.");
            var exactSizeCodec = codec as IRpcSizedCodec<T>;
            if (exactSizeCodec is not null && !exactSizeCodec.CanExactSize) exactSizeCodec = null;
            enumerator = stream.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PendingCalls.TryAcceptProducerProgress(requestId, deadline))
                    throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "RPC deadline exceeded during client stream production.");
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                await Session.SendClientStreamChunkAsync(requestId, streamId, enumerator.Current, codec,
                    exactSizeCodec, deadline, _timeProvider, cancellationToken, group).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!PendingCalls.TryAcceptProducerProgress(requestId, deadline))
                throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "RPC deadline exceeded before client stream completion.");
            publishingTerminal = true;
            Session.SendClientStreamComplete(requestId, streamId, deadline, _timeProvider, cancellationToken, group);
        }
        catch (Exception error)
        {
            if (publishingTerminal) independent = error; else business = error;
            independent = PublishProducerError(group, streamId, error, independent, deadline, cancellationToken);
            try { PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, error); }
            catch (Exception cleanup) { independent = ClientStreamPublicationGroup.AddPhysical(independent, cleanup); }
        }
        finally
        {
            if (enumerator is not null)
            {
                try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    // Cleanup provenance is independent even when the exact same object/token
                    // was also the logical primary. Never infer stage ownership from exception trees.
                    independent = ClientStreamPublicationGroup.AddPhysical(independent, cleanup);
                    independent = PublishProducerError(group, streamId, cleanup, independent, deadline, cancellationToken);
                    try { PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, cleanup); }
                    catch (Exception terminalFailure) { independent = ClientStreamPublicationGroup.AddPhysical(independent, terminalFailure); }
                }
            }
        }
        // Raw outcomes precede async OCE conversion. The same Task is returned to writer/group;
        // its canceled status never substitutes for this fixed-slot physical provenance.
        try { group.ReportProducerOutcome(streamId, business, independent); }
        catch (Exception reportingFailure)
        {
            var unreported = ClientStreamPublicationGroup.AddPhysical(
                ClientStreamPublicationGroup.AddPhysical(business, independent), reportingFailure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unreported!).Throw();
        }
        // The writer observes the business boundary when one exists. Independent cleanup is
        // already owned by the exact slot; wrapping both here would destroy that provenance
        // when an ordinary writer simply awaits this same Task.
        var failure = business ?? independent;
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private Exception? PublishProducerError(ClientStreamPublicationGroup group, ushort streamId,
        Exception error, Exception? independent, RpcDeadline deadline, CancellationToken cancellationToken)
    {
        // Once logical terminal won, bounded host cleanup owns terminal publication. Do not
        // manufacture a second independent failure merely by attempting with its canceled token.
        if (cancellationToken.IsCancellationRequested || !PendingCalls.Contains(group.RequestId)) return independent;
        try
        {
            var protocolError = error as SharpLinkException ?? new SharpLinkException(
                SharpLinkErrorCode.Internal, "Internal client stream error.", error);
            Session.SendClientStreamError(group.RequestId, streamId, protocolError, deadline,
                _timeProvider, cancellationToken, group);
        }
        catch (Exception sendError) { independent = ClientStreamPublicationGroup.AddPhysical(independent, sendError); }
        return independent;
    }

    public ValueTask OnConsumerAbandonedAsync(
        long requestId,
        IStreamDispatchState? dispatchState)
    {
        if (PendingCalls.TryComplete(requestId, PendingCallCompletionReason.ConsumerAbandoned))
            return ValueTask.CompletedTask;

        Session.StreamManager.Unregister(requestId, 0);
        if (dispatchState is null || dispatchState.IsDetached)
        {
            if (Session.IsConnected)
                TrySendCancel(requestId, ProtocolV2CancelReason.ConsumerAbandoned);
            return ValueTask.CompletedTask;
        }

        if (!Session.IsConnected)
            return ValueTask.CompletedTask;

        return AwaitRemoteCompletionAndSendCancelAsync(requestId, dispatchState);
    }

    void IPendingCallOwner.OnPendingCallCompleted(in PendingCallCompletion completion)
    {
        var shouldSendCancel = completion.Reason is
            PendingCallCompletionReason.UserCancellation or
            PendingCallCompletionReason.DeadlineExceeded or
            PendingCallCompletionReason.ConsumerAbandoned;
        // Local cleanup follows shouldSendCancel - a dispatcher must be torn down even when the
        // Request never reached the peer. Only the transport cancel is gated on publication: a
        // cancel for a Request the peer never received is discarded by it, and emitting one would
        // also let the cancel overtake a Request this connection is still serializing.
        var shouldNotifyPeer = shouldSendCancel && completion.RequestPublished;

        if (completion.Kind is PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming)
        {
            if (completion.Reason == PendingCallCompletionReason.ConsumerAbandoned)
            {
                Session.StreamManager.Unregister(completion.RequestId, 0);
                completion.Dispatcher?.Complete(completion.Exception);
            }
            else if (shouldSendCancel)
            {
                var localAbort = completion.Dispatcher as IStreamLocalAbortDispatcher;
                ValueTask drain;
                try
                {
                    localAbort?.CompleteLocalAbort(completion.Exception);
                    drain = Session.StreamManager.CompleteStreamAfterDispatchesAsync(
                        completion.RequestId,
                        0,
                        completion.Exception);
                    if (drain.IsCompletedSuccessfully)
                        localAbort?.RetireLocalAbortBuffer();
                }
                catch (Exception exception)
                {
                    try
                    {
                        Fail(exception);
                    }
                    finally
                    {
                        _client.HandleConnectionFatalFailure(this, exception);
                    }
                    return;
                }
                if (!drain.IsCompletedSuccessfully)
                {
                    Interlocked.Increment(ref _auxiliaryActiveCallCount);
                    try
                    {
                        _client.TrackFrameworkTask(
                            FinishCancellationAfterDispatchesAsync(
                                drain,
                                completion.RequestId,
                                GetCancelReason(completion.Reason),
                                shouldNotifyPeer,
                                localAbort),
                            "CancellationDispatchCleanup");
                    }
                    catch
                    {
                        ReleaseAuxiliaryActiveCall();
                        throw;
                    }
                    return;
                }
            }
            else
            {
                Session.StreamManager.CompleteStream(
                    completion.RequestId,
                    0,
                    completion.Exception);
            }
        }

        if (shouldNotifyPeer)
            TrySendCancel(completion.RequestId, GetCancelReason(completion.Reason));
    }

    void IPendingCallOwner.OnPendingCallCompleted(in PendingCallCompletion completion,
        ref StreamSinkPublicationDisposition disposition, long dispatcherLease, ClientStreamPublicationGroup? group)
    {
        if (completion.Kind != PendingCallKind.DuplexStreaming)
        {
            ((IPendingCallOwner)this).OnPendingCallCompleted(in completion);
            return;
        }
        var cancel = completion.Reason is PendingCallCompletionReason.UserCancellation or
            PendingCallCompletionReason.DeadlineExceeded or PendingCallCompletionReason.ConsumerAbandoned;
        var receipt = completion.Dispatcher as IStreamSinkPublicationReceipt;
        var exactRetentionFailed = receipt is not null && dispatcherLease == 0;
        // A failed exact lease is NOT a custom/unknown dispatcher. Never mutate or retire the
        // captured pooled object without its original generation proof. The exact full-ID route
        // still owns its independent manager cleanup; a live attached route prevents pool reuse.
        var localAbort = exactRetentionFailed ? null : completion.Dispatcher as IStreamLocalAbortDispatcher;
        Exception? failure = exactRetentionFailed
            ? new InvalidOperationException("The original Duplex sink publication lease could not be retained.") : null;
        // The caller already retained the exact dispatcher lease before observers. Publication
        // records the receipt BEFORE arbitrary signal/credit callbacks; route cleanup is separate.
        try
        {
            if (receipt is not null && dispatcherLease != 0)
                receipt.PublishSinkTerminal(completion.Exception,
                    cancel && completion.Reason != PendingCallCompletionReason.ConsumerAbandoned,
                    dispatcherLease, ref disposition);
            else if (!exactRetentionFailed)
            {
                if (cancel && completion.Reason != PendingCallCompletionReason.ConsumerAbandoned)
                    localAbort?.CompleteLocalAbort(completion.Exception);
                else completion.Dispatcher?.Complete(completion.Exception);
            }
        }
        catch (Exception error) { failure = error; }
        if (group?.HasSupervisionAnchor == true)
        {
            // Every producing Duplex terminal owns the exact route decision. Synchronous
            // settled/drained success does NOT gain an auxiliary counter or Task allocation.
            // Only cancellation terminals have a peer cancel reason. Resolve this callback-free
            // scalar before claiming a cleanup branch/charge; ordinary EOF/close needs none.
            var cancelReason = cancel ? GetCancelReason(completion.Reason) : default;
            var publication = group.WaitForResponseRoutePublication();
            ValueTask drain = default;
            var waitingForPublication = !publication.IsCompleted;
            if (!waitingForPublication)
            {
                try { drain = Session.StreamManager.CompleteStreamAfterDispatchesAsync(completion.RequestId, 0, completion.Exception); }
                catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
            }
            if (waitingForPublication || !drain.IsCompletedSuccessfully)
            {
                group.ClaimResponseDrain();
                Interlocked.Increment(ref _auxiliaryActiveCallCount);
                var cleanup = FinishAnchoredDuplexRouteAsync(waitingForPublication ? publication : null, drain,
                    completion.RequestId, completion.Exception, cancel,
                    completion.Reason != PendingCallCompletionReason.ConsumerAbandoned ? localAbort : null,
                    cancelReason, cancel && completion.RequestPublished);
                group.AttachResponseDrain(cleanup);
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                return;
            }
            if (cancel && completion.Reason != PendingCallCompletionReason.ConsumerAbandoned)
            {
                try { localAbort?.RetireLocalAbortBuffer(); }
                catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
            }
            if (cancel && completion.RequestPublished)
            {
                try { TrySendCancel(completion.RequestId, GetCancelReason(completion.Reason)); }
                catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
            }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return;
        }
        if (cancel && completion.Reason != PendingCallCompletionReason.ConsumerAbandoned)
        {
            ValueTask drain = default;
            var hasDrain = false;
            try
            {
                drain = Session.StreamManager.CompleteStreamAfterDispatchesAsync(completion.RequestId, 0, completion.Exception);
                hasDrain = true;
            }
            catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
            if (hasDrain && !drain.IsCompletedSuccessfully)
            {
                Interlocked.Increment(ref _auxiliaryActiveCallCount);
                var cleanup = FinishProducingCancellationAfterDispatchesAsync(drain, completion.RequestId,
                    GetCancelReason(completion.Reason), completion.RequestPublished, localAbort);
                try
                {
                    _client.TrackFrameworkTask(cleanup, "CancellationDispatchCleanup");
                }
                catch (Exception error)
                {
                    failure = ClientStreamPublicationGroup.AddPhysical(failure,
                        error is PhysicalTaskFailureException physical ? physical.RawException : error);
                }
                // Cleanup owns retirement, Cancel and the original auxiliary charge even if tracking fails.
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                return;
            }
            try { localAbort?.RetireLocalAbortBuffer(); }
            catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
        }
        else
        {
            try
            {
                if (completion.Reason == PendingCallCompletionReason.ConsumerAbandoned)
                    Session.StreamManager.Unregister(completion.RequestId, 0);
                else Session.StreamManager.CompleteStream(completion.RequestId, 0, completion.Exception);
            }
            catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
        }
        if (cancel && completion.RequestPublished)
        {
            try { TrySendCancel(completion.RequestId, GetCancelReason(completion.Reason)); }
            catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task FinishAnchoredDuplexRouteAsync(Task? routePublication, ValueTask drain, long requestId,
        Exception? terminal, bool cancel, IStreamLocalAbortDispatcher? localAbort,
        ProtocolV2CancelReason cancelReason, bool notifyPeer)
    {
        Exception? failure = null;
        if (routePublication is not null)
        {
            try { await routePublication.ConfigureAwait(false); }
            catch (Exception error) { failure = PhysicalTaskFailureException.GetRaw(error); }
            try { drain = Session.StreamManager.CompleteStreamAfterDispatchesAsync(requestId, 0, terminal); }
            catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
        }
        // Runtime's exact removed-entry result owns all acquired dispatches even when Complete
        // throws. Its failure is delivered only after real drain/finalization, never a proxy.
        try { await drain.ConfigureAwait(false); }
        catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, PhysicalTaskFailureException.GetRaw(error)); }
        if (cancel)
        {
            try { localAbort?.RetireLocalAbortBuffer(); }
            catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
        }
        if (notifyPeer)
        {
            try { TrySendCancel(requestId, cancelReason); }
            catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
        }
        try { ReleaseAuxiliaryActiveCall(); }
        catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
        if (failure is not null) throw PhysicalTaskFailureException.From(failure);
    }

    private async Task FinishProducingCancellationAfterDispatchesAsync(ValueTask drain, long requestId,
        ProtocolV2CancelReason reason, bool notifyPeer, IStreamLocalAbortDispatcher? localAbort)
    {
        Exception? failure = null;
        try { await drain.ConfigureAwait(false); }
        catch (Exception error) { failure = PhysicalTaskFailureException.GetRaw(error); }
        try { localAbort?.RetireLocalAbortBuffer(); }
        catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
        if (notifyPeer)
        {
            try { TrySendCancel(requestId, reason); }
            catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
        }
        try { ReleaseAuxiliaryActiveCall(); }
        catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
        if (failure is not null) throw PhysicalTaskFailureException.From(failure);
    }

    void IPendingCallOwner.OnProducerCancellationCallbackFailed(Exception exception)
        => _client.ReportProducerCancellationCallbackFailure(exception);

    void IPendingCallOwner.OnRequestIdsExhausted()
        => OnProtocolIdentityExhausted();

    void IPendingCallOwner.OnPendingCallCapacityIdle()
    {
        _client.TryAdvancePlannedSessionRefreshRetirement(this);
        _client.RetireDrainingConnectionIfIdle(this);
    }

    private async Task FinishCancellationAfterDispatchesAsync(
        ValueTask drain,
        long requestId,
        ProtocolV2CancelReason reason,
        bool notifyPeer,
        IStreamLocalAbortDispatcher? localAbort)
    {
        try
        {
            await drain.ConfigureAwait(false);
            localAbort?.RetireLocalAbortBuffer();
            if (notifyPeer)
                TrySendCancel(requestId, reason);
        }
        catch (Exception exception)
        {
            try
            {
                Fail(exception);
            }
            finally
            {
                _client.HandleConnectionFatalFailure(this, exception);
            }
        }
        finally
        {
            ReleaseAuxiliaryActiveCall();
        }
    }

    private async ValueTask AwaitRemoteCompletionAndSendCancelAsync(
        long requestId,
        IStreamDispatchState dispatchState)
    {
        try
        {
            await dispatchState.WaitForDetachedAsync(Session.LifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!Session.IsConnected)
        {
            return;
        }

        if (Session.IsConnected)
            TrySendCancel(requestId, ProtocolV2CancelReason.ConsumerAbandoned);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return Session.DisposeAsync();

        Fail(new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "Client connection is disposed."));
        Binding.Close(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Client connection is disposed."));
        _cancellation.Dispose();
        PendingCalls.Dispose();
        return Session.DisposeAsync();
    }

    private void ReleaseAuxiliaryActiveCall()
    {
        var remaining = Interlocked.Decrement(ref _auxiliaryActiveCallCount);
        if (remaining < 0)
            throw new InvalidOperationException("Client connection auxiliary active call count underflowed.");
        if (remaining == 0)
        {
            _client.TryAdvancePlannedSessionRefreshRetirement(this);
            _client.RetireDrainingConnectionIfIdle(this);
        }
    }

    private static ProtocolV2CancelReason GetCancelReason(PendingCallCompletionReason reason)
        => reason switch
        {
            PendingCallCompletionReason.UserCancellation => ProtocolV2CancelReason.UserCancellation,
            PendingCallCompletionReason.DeadlineExceeded => ProtocolV2CancelReason.DeadlineExceeded,
            PendingCallCompletionReason.ConsumerAbandoned => ProtocolV2CancelReason.ConsumerAbandoned,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        };

    /// <summary>
    /// Tells the peer to stop a call whose Request is already queued but whose caller gave up.
    /// </summary>
    /// <remarks>
    /// Untracked dispatch shapes (plain OneWay) hold no pending entry, so nothing else observes the
    /// terminal reason and therefore nothing else can publish the cancel.
    /// </remarks>
    internal void TrySendDeadlineCancel(long requestId)
        => TrySendCancel(requestId, ProtocolV2CancelReason.DeadlineExceeded);

    private void TrySendCancel(long requestId, ProtocolV2CancelReason reason)
    {
        try
        {
            Session.SendCancelAsync(requestId, reason);
        }
        catch (SharpLinkException exception) when (exception.Code is
            SharpLinkErrorCode.ConnectionClosed or
            SharpLinkErrorCode.ResourceExhausted or
            SharpLinkErrorCode.Unavailable)
        {
        }
    }
}

internal sealed partial class SharpLinkClient
{
    internal void ReportConnectionCancellationCallbackFailure(Exception exception)
        => _logger.LogError(exception, "SharpLink connection cancellation callback failed during teardown.");

    internal void ReportProducerCancellationCallbackFailure(Exception exception)
        => _logger.LogError(exception, "SharpLink client-stream producer cancellation callback failed during teardown.");
}

internal struct LateResponseLogLimiter
{
    private readonly long _intervalTimestampTicks;
    private long _nextLogTimestamp;
    private int _suppressedCount;

    internal LateResponseLogLimiter(long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        _intervalTimestampTicks = timestampFrequency > long.MaxValue / 5
            ? long.MaxValue
            : timestampFrequency * 5;
        _nextLogTimestamp = long.MinValue;
    }

    internal long IntervalTimestampTicks => _intervalTimestampTicks;

    internal bool ShouldLog(long timestamp, out int suppressedCount)
    {
        while (true)
        {
            var next = Volatile.Read(ref _nextLogTimestamp);
            if (next != 0 && timestamp < next)
            {
                Interlocked.Increment(ref _suppressedCount);
                suppressedCount = 0;
                return false;
            }

            var newNext = timestamp > long.MaxValue - _intervalTimestampTicks
                ? long.MaxValue
                : timestamp + _intervalTimestampTicks;
            if (Interlocked.CompareExchange(ref _nextLogTimestamp, newNext, next) != next)
                continue;

            suppressedCount = Interlocked.Exchange(ref _suppressedCount, 0);
            return true;
        }
    }
}
