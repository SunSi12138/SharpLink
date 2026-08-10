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
    IAsyncDisposable
{
    private readonly SharpLinkClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cancellation;
    private readonly Func<long, IStreamDispatchState?, ValueTask> _consumerAbandonedCallback;
    private LateResponseLogLimiter _lateResponseLogLimiter;
    private int _state = (int)ClientConnectionState.Ready;
    private int _activeCallCount;
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

    /// <summary>Gets the owning endpoint identity when this connection belongs to a cluster.</summary>
    public string? EndpointId { get; }

    /// <summary>Gets the owning endpoint generation when this connection belongs to a dynamic cluster.</summary>
    public long EndpointGeneration { get; }

    public ClientConnectionState State
        => (ClientConnectionState)Volatile.Read(ref _state);

    public bool CanAcceptCalls
        => State == ClientConnectionState.Ready && Session.CanAcceptCalls;

    public int ActiveCallCount => Volatile.Read(ref _activeCallCount);

    /// <summary>
    /// Validates a stable connection lifecycle snapshot at a transition or test boundary.
    /// This intentionally stays outside the per-frame and selection hot paths.
    /// </summary>
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
        var previousState = Interlocked.Exchange(ref _state, (int)ClientConnectionState.Closed);
        if (previousState == (int)ClientConnectionState.Closed)
        {
            return;
        }
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
        if (!CanAcceptCalls)
            return false;

        Interlocked.Increment(ref _activeCallCount);
        if (CanAcceptCalls)
            return true;

        ReleaseActiveCall();
        return false;
    }

    public void EndUntrackedCall() => ReleaseActiveCall();

    public async Task SendClientStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        CancellationToken cancellationToken = default)
    {
        if (!PendingCalls.Contains(requestId))
            throw new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "The owning RPC call is no longer active.");

        try
        {
            await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await Session.SendStreamChunkAsync(
                    requestId,
                    streamId,
                    item,
                    cancellationToken).ConfigureAwait(false);
            }

            Session.SendStreamCompleteAsync(requestId, streamId);
        }
        catch (Exception exception)
        {
            try
            {
                var protocolError = exception as SharpLinkException ?? new SharpLinkException(
                    SharpLinkErrorCode.Internal,
                    "Internal client stream error.",
                    exception);
                Session.SendStreamErrorAsync(requestId, streamId, protocolError);
            }
            catch (SharpLinkException sendException) when (sendException.Code is
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

        // A response/complete path may already own the pending slot but not yet have
        // flushed receive credit and detached its dispatcher. Remove the map entry if it
        // is still published, then join the winning completion before a late Cancel.
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
        => Interlocked.Increment(ref _activeCallCount);

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
                ValueTask drain;
                try
                {
                    drain = Session.StreamManager is StreamManager manager
                        ? manager.CompleteStreamAfterDispatchesAsync(
                            completion.RequestId,
                            0,
                            completion.Exception)
                        : ValueTask.CompletedTask;
                }
                catch (Exception exception)
                {
                    Fail(exception);
                    ReleaseActiveCall();
                    return;
                }
                if (!drain.IsCompletedSuccessfully)
                {
                    _client.TrackFrameworkTask(
                        FinishCancellationAfterDispatchesAsync(
                            drain,
                            completion.RequestId,
                            GetCancelReason(completion.Reason)),
                        "CancellationDispatchCleanup");
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

        // Return all receive credit before Cancel. Both frames share the session send pump,
        // so the peer observes the final WindowUpdate before it reclaims the aborted stream.
        if (shouldSendCancel)
            TrySendCancel(completion.RequestId, GetCancelReason(completion.Reason));

        ReleaseActiveCall();
    }

    void IPendingCallOwner.OnProducerCancellationCallbackFailed(Exception exception)
        => _client.ReportProducerCancellationCallbackFailure(exception);

    private async Task FinishCancellationAfterDispatchesAsync(
        ValueTask drain,
        long requestId,
        ProtocolV2CancelReason reason)
    {
        try
        {
            await drain.ConfigureAwait(false);
            TrySendCancel(requestId, reason);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            ReleaseActiveCall();
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

    private void ReleaseActiveCall()
    {
        var remaining = Interlocked.Decrement(ref _activeCallCount);
        if (remaining < 0)
            throw new InvalidOperationException("Client connection active call count underflowed.");
        if (remaining == 0)
            _client.RetireDrainingConnectionIfIdle(this);
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
        => _logger.LogError(exception, "SharpLink client-stream producer cancellation callback failed.");
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
