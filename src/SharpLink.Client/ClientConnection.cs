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
    IRpcClientStreamSink,
    IStreamConsumerDeliveryGate,
    IAsyncDisposable
{
    // Session-refresh cut state machine. A fatal transition and the blue-green commit claim
    // race on this single CAS target so the source eligibility cut is linearized against a
    // replacement that fails before the cut.
    private const int SessionRefreshCommitOpen = 0;
    private const int SessionRefreshCommitCommitted = 1;
    private const int SessionRefreshCommitRejected = 2;

    private readonly SharpLinkClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cancellation;
    private readonly Func<long, IStreamDispatchState?, ValueTask> _consumerAbandonedCallback;
    private LateResponseLogLimiter _lateResponseLogLimiter;
    private int _state = (int)ClientConnectionState.Ready;
    private int _auxiliaryActiveCallCount;
    private int _callAdmissionReservations;
    private int _callAdmissionClosed;
    private int _selectionEligible = 1;
    private int _fatalFailureObservedForAdmission;
    private int _plannedSessionRefreshRetirement;
    private int _sessionRefreshCommitState;
    private SessionRefreshRedirect? _sessionRefreshRedirect;
    private int _disposed;

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
        PendingCalls = new PendingRequestTable(
            maxPendingCalls,
            runtimeContext.Codecs,
            this,
            runtimeContext.TimeProvider);
        EndpointId = endpointId;
        EndpointGeneration = endpointGeneration;
    }

    public RpcSession Session { get; }
    public PendingRequestTable PendingCalls { get; }
    public string? EndpointId { get; }
    public long EndpointGeneration { get; }

    public ClientConnectionState State
        => (ClientConnectionState)Volatile.Read(ref _state);

    public bool CanAcceptCalls
        => Volatile.Read(ref _selectionEligible) != 0 &&
           State == ClientConnectionState.Ready &&
           Session.CanAcceptCalls;

    public int ActiveCallCount
        => PendingCalls.ActiveCount + Volatile.Read(ref _auxiliaryActiveCallCount);

    internal int CallAdmissionReservationCount => Volatile.Read(ref _callAdmissionReservations);

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
        // The source cut is linearized later by TryCommitSessionRefreshRetirement(); this hook
        // deterministically freezes the window in between for review regressions.
        _client.NotifyAfterSessionRefreshCommitReservationForTest(this);
        return true;
    }

    /// <summary>
    /// Claims the blue-green eligibility cut for a replacement that already holds a session
    /// refresh admission reservation.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the commit won the race against a fatal transition and the
    /// caller must retire the source. <see langword="false"/> when a fatal transition was
    /// observed first, in which case the caller must roll the replacement back and keep the
    /// source selectable.
    /// </returns>
    internal bool TryCommitSessionRefreshRetirement()
    {
        // The flag read is the linearization point of the claim: an observation published before
        // it rejects the cut, while an observation published after it falls into ordinary
        // post-cut replacement handling. The CAS then rejects a commit whose observation landed
        // between this read and the claim.
        if (Volatile.Read(ref _fatalFailureObservedForAdmission) != 0)
            return false;
        return Interlocked.CompareExchange(
            ref _sessionRefreshCommitState,
            SessionRefreshCommitCommitted,
            SessionRefreshCommitOpen) == SessionRefreshCommitOpen;
    }

    internal void ObserveFatalFailureForAdmission()
    {
        Volatile.Write(ref _fatalFailureObservedForAdmission, 1);
        // A commit that already won keeps its claim: the cut linearized first, so the failure is
        // handled with ordinary post-cut replacement semantics. Any unclaimed commit is rejected.
        Interlocked.CompareExchange(
            ref _sessionRefreshCommitState,
            SessionRefreshCommitRejected,
            SessionRefreshCommitOpen);
    }

    private bool TryReserveOwnCallAdmission(bool notifyTestHook)
    {
        if (Volatile.Read(ref _fatalFailureObservedForAdmission) != 0 ||
            Volatile.Read(ref _callAdmissionClosed) != 0 ||
            State != ClientConnectionState.Ready || !Session.CanAcceptCalls)
        {
            return false;
        }

        Interlocked.Increment(ref _callAdmissionReservations);
        if (Volatile.Read(ref _fatalFailureObservedForAdmission) != 0 ||
            Volatile.Read(ref _callAdmissionClosed) != 0 ||
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
    /// Releases one anonymous selection reservation if present. Pending-call registration invokes
    /// this as a transfer into pending capacity; test-only direct table registrations therefore
    /// remain valid when no selection reservation exists.
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
                _client.TryAdvancePlannedSessionRefreshRetirement(this);
            return;
        }
    }

    internal void BeginPlannedSessionRefreshRetirement(ClientConnection replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (ReferenceEquals(this, replacement))
            throw new ArgumentException("A session-refresh source cannot replace itself.", nameof(replacement));

        // All connections in one refresh lineage share a single indirection and it always points
        // at the newest Ready replacement. Disposed predecessors are therefore not retained by an
        // older pinned source and a stale admission never exhausts a fixed redirect hop budget.
        var redirect = GetOrCreateSessionRefreshRedirect();
        replacement.JoinSessionRefreshRedirect(redirect);
        redirect.Publish(replacement);

        Volatile.Write(ref _selectionEligible, 0);
        Volatile.Write(ref _callAdmissionClosed, 1);
        Volatile.Write(ref _plannedSessionRefreshRetirement, 1);
    }

    internal void JoinSessionRefreshRedirect(SessionRefreshRedirect redirect)
    {
        ArgumentNullException.ThrowIfNull(redirect);
        Interlocked.CompareExchange(ref _sessionRefreshRedirect, redirect, null);
    }

    private SessionRefreshRedirect GetOrCreateSessionRefreshRedirect()
    {
        var existing = Volatile.Read(ref _sessionRefreshRedirect);
        if (existing is not null)
            return existing;

        var created = new SessionRefreshRedirect();
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
        if (state == ClientConnectionState.Draining && sessionAcceptsCalls)
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

    public bool MarkDraining()
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)ClientConnectionState.Draining,
                (int)ClientConnectionState.Ready) == (int)ClientConnectionState.Ready)
        {
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
        Volatile.Write(ref _selectionEligible, 0);
        Volatile.Write(ref _callAdmissionClosed, 1);
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
        if (State == ClientConnectionState.Ready && Session.IsConnected)
            return true;

        ReleaseAuxiliaryActiveCall();
        return false;
    }

    public void EndUntrackedCall() => ReleaseAuxiliaryActiveCall();

    public async Task SendClientStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(codec);
        cancellationToken.ThrowIfCancellationRequested();
        if (!PendingCalls.TryGetProducerDeadline(requestId, out var deadline))
            throw new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "The owning RPC call is no longer active.");

        try
        {
            await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PendingCalls.TryAcceptProducerProgress(requestId))
                    throw new SharpLinkException(
                        SharpLinkErrorCode.DeadlineExceeded,
                        "RPC deadline exceeded during client stream production.");

                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    break;

                await Session.SendClientStreamChunkAsync(
                    requestId,
                    streamId,
                    enumerator.Current,
                    codec,
                    deadline,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!PendingCalls.TryAcceptProducerProgress(requestId))
                throw new SharpLinkException(
                    SharpLinkErrorCode.DeadlineExceeded,
                    "RPC deadline exceeded before client stream completion.");
            Session.SendClientStreamComplete(
                requestId,
                streamId,
                deadline,
                _timeProvider,
                cancellationToken);
        }
        catch (Exception exception)
        {
            try
            {
                var protocolError = exception as SharpLinkException ?? new SharpLinkException(
                    SharpLinkErrorCode.Internal,
                    "Internal client stream error.",
                    exception);
                Session.SendClientStreamError(
                    requestId,
                    streamId,
                    protocolError,
                    deadline,
                    _timeProvider,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (SharpLinkException sendException) when (sendException.Code is
                SharpLinkErrorCode.DeadlineExceeded or
                SharpLinkErrorCode.ConnectionClosed or
                SharpLinkErrorCode.ResourceExhausted or
                SharpLinkErrorCode.Unavailable)
            {
            }
            throw;
        }
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

    void IPendingCallOwner.OnPendingCallRegistered()
    {
        ReleaseCallAdmissionReservation();
    }

    void IPendingCallOwner.OnPendingCallCompleted(in PendingCallCompletion completion)
    {
        var shouldSendCancel = completion.Reason is
            PendingCallCompletionReason.UserCancellation or
            PendingCallCompletionReason.DeadlineExceeded or
            PendingCallCompletionReason.ConsumerAbandoned;

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

        if (shouldSendCancel)
            TrySendCancel(completion.RequestId, GetCancelReason(completion.Reason));
    }

    void IPendingCallOwner.OnProducerCancellationCallbackFailed(Exception exception)
        => _client.ReportProducerCancellationCallbackFailure(exception);

    void IPendingCallOwner.OnPendingCallCapacityIdle()
    {
        _client.TryAdvancePlannedSessionRefreshRetirement(this);
        _client.RetireDrainingConnectionIfIdle(this);
    }

    private async Task FinishCancellationAfterDispatchesAsync(
        ValueTask drain,
        long requestId,
        ProtocolV2CancelReason reason,
        IStreamLocalAbortDispatcher? localAbort)
    {
        try
        {
            await drain.ConfigureAwait(false);
            localAbort?.RetireLocalAbortBuffer();
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
