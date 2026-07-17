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
    private readonly CancellationTokenSource _cancellation;
    private readonly Action<long> _consumerAbandonedCallback;
    private int _state = (int)ClientConnectionState.Ready;
    private int _activeCallCount;
    private int _disposed;

    public ClientConnection(
        SharpLinkClient client,
        RpcSession session,
        CancellationTokenSource cancellation,
        int maxPendingCalls,
        IRpcCodecProvider codecs)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        _consumerAbandonedCallback = OnConsumerAbandoned;
        PendingCalls = new PendingRequestTable(maxPendingCalls, codecs, this);
    }

    public RpcSession Session { get; }

    public PendingRequestTable PendingCalls { get; }

    public ClientConnectionState State
        => (ClientConnectionState)Volatile.Read(ref _state);

    public bool CanAcceptCalls
        => State == ClientConnectionState.Ready && Session.CanAcceptCalls;

    public int ActiveCallCount => Volatile.Read(ref _activeCallCount);

    public CancellationToken CancellationToken => _cancellation.Token;

    public Action<long> ConsumerAbandonedCallback => _consumerAbandonedCallback;

    public void MarkDraining()
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)ClientConnectionState.Draining,
                (int)ClientConnectionState.Ready) == (int)ClientConnectionState.Ready)
        {
            Session.MarkDraining();
        }
    }

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.Exchange(ref _state, (int)ClientConnectionState.Closed) ==
            (int)ClientConnectionState.Closed)
        {
            return;
        }

        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        PendingCalls.FailAllPendingRequests(exception);
        Session.StreamManager.CompleteAll(exception);
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
            await foreach (var item in stream.WithCancellation(cancellationToken))
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
                Session.SendStreamErrorAsync(requestId, streamId, exception);
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

    public void OnConsumerAbandoned(long requestId)
    {
        if (!PendingCalls.TryComplete(requestId, PendingCallCompletionReason.ConsumerAbandoned))
        {
            // A response/complete path may already own the pending slot but not yet have
            // detached its dispatcher. Remove the map entry here so this lease cannot be
            // returned to the process-wide pool while that completion callback is delayed.
            Session.StreamManager.Unregister(requestId, 0);
        }
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
            TrySendCancel(completion.RequestId);

        ReleaseActiveCall();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Fail(new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "Client connection is disposed."));
        _cancellation.Dispose();
        PendingCalls.Dispose();
        await Session.DisposeAsync().ConfigureAwait(false);
    }

    private void ReleaseActiveCall()
    {
        var remaining = Interlocked.Decrement(ref _activeCallCount);
        if (remaining < 0)
            throw new InvalidOperationException("Client connection active call count underflowed.");
        if (remaining == 0)
            _client.RetireDrainingConnectionIfIdle(this);
    }

    private void TrySendCancel(long requestId)
    {
        try
        {
            Session.SendCancelAsync(requestId);
        }
        catch (SharpLinkException exception) when (exception.Code is
            SharpLinkErrorCode.ConnectionClosed or
            SharpLinkErrorCode.ResourceExhausted or
            SharpLinkErrorCode.Unavailable)
        {
        }
    }
}
