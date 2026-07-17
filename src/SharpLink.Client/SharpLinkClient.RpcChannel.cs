


namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private static readonly Action<object?> SRequestCancelCallback = static state =>
    {
        var cancelState = (RequestCancelState)state!;
        if (!cancelState.TryBeginInvocation())
            return;

        try
        {
            cancelState.Client.OnRequestCancel(cancelState);
        }
        finally
        {
            cancelState.ReturnAfterInvocation();
        }
    };

    private static readonly Action<object?> SStreamCancelCallback = static state =>
    {
        var cancelState = (StreamCancelState)state!;
        if (!cancelState.TryBeginInvocation())
            return;

        try
        {
            cancelState.Client.OnStreamCancel(cancelState);
        }
        finally
        {
            cancelState.ReturnAfterInvocation();
        }
    };

    private static readonly Action<object?> SRequestTimeoutCallback = static state =>
    {
        var timeoutState = (RequestTimeoutState)state!;
        if (!timeoutState.TryBeginInvocation())
            return;

        try
        {
            timeoutState.Client.OnRequestTimeout(timeoutState);
        }
        finally
        {
            timeoutState.ReturnAfterInvocation();
        }
    };

    private static readonly Action<object?> SStreamTimeoutCallback = static state =>
    {
        var timeoutState = (StreamTimeoutState)state!;
        if (!timeoutState.TryBeginInvocation())
            return;

        try
        {
            timeoutState.Client.OnStreamTimeout(timeoutState);
        }
        finally
        {
            timeoutState.ReturnAfterInvocation();
        }
    };

    private PooledCancellationRegistration RegisterCancel(
        CancellationToken ct,
        long requestId,
        bool isOneWay,
        CancellationToken userToken)
    {
        if (!ct.CanBeCanceled)
            return default;

        var state = RequestCancelState.Rent(this, requestId, isOneWay, userToken);
        var registration = ct.UnsafeRegister(SRequestCancelCallback, state);
        return new PooledCancellationRegistration(registration, state);
    }

    private PooledCancellationRegistration RegisterStreamCancel(
        CancellationToken ct,
        long requestId,
        CancellationToken userToken)
    {
        if (!ct.CanBeCanceled)
            return default;

        var state = StreamCancelState.Rent(this, requestId, userToken);
        var registration = ct.UnsafeRegister(SStreamCancelCallback, state);
        return new PooledCancellationRegistration(registration, state);
    }

    private static OperationCanceledException CreateCancellationException(CancellationToken userToken)
    {
        return userToken.CanBeCanceled
            ? new OperationCanceledException(userToken)
            : new OperationCanceledException();
    }

    private void SendRpcCall(
        RpcSession session,
        long interfaceHash,
        long methodHash,
        long requestId,
        ProtocolV2FrameFlags flags,
        Action<IBufferWriter<byte>>? payloadWriter,
        DateTimeOffset? deadline = null,
        SharpLinkMetadata? metadata = null)
    {
        var hasMetadata = metadata is { Count: > 0 };
        var metadataLength = 0;
        if (deadline is not null)
            flags |= ProtocolV2FrameFlags.HasDeadline;
        if (hasMetadata)
        {
            if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.Metadata) == 0)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.Unimplemented,
                    "The connected server did not negotiate request metadata support.");
            }
            metadataLength = ProtocolV2PayloadCodec.GetMetadataPayloadLength(metadata!);
            if (metadataLength > _protocolOptions.MaxMetadataBytes)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    $"Request metadata exceeds {_protocolOptions.MaxMetadataBytes} bytes.");
            }
            flags |= ProtocolV2FrameFlags.HasMetadata;
        }

        var writer = session.RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.Request, flags, unchecked((ulong)requestId)))
            {
                var span = writer.GetSpan(ProtocolV2Constants.RequestPrefixBytes);
                BinaryPrimitives.WriteInt64LittleEndian(span, interfaceHash);
                BinaryPrimitives.WriteInt64LittleEndian(span[8..], methodHash);
                writer.Advance(ProtocolV2Constants.RequestPrefixBytes);
                if (deadline is { } absoluteDeadline)
                {
                    var deadlineSpan = writer.GetSpan(sizeof(long));
                    BinaryPrimitives.WriteInt64LittleEndian(
                        deadlineSpan,
                        absoluteDeadline.ToUnixTimeMilliseconds());
                    writer.Advance(sizeof(long));
                }
                if (hasMetadata)
                {
                    ProtocolV2PayloadCodec.WriteVarUInt32(writer, checked((uint)metadataLength));
                    ProtocolV2PayloadCodec.WriteMetadata(writer, metadata!);
                }
                payloadWriter?.Invoke(writer);
            }

            // SendPacket takes ownership even when enqueueing detects a terminal session.
            ownsWriter = false;
            session.SendPacket(writer);
        }
        finally
        {
            if (ownsWriter)
                _runtimeContext.Buffers.Return(writer);
        }
    }

    public async Task SendClientStreamAsync<T>(long requestId, ushort streamId, IAsyncEnumerable<T> stream, CancellationToken cancellationToken = default)
    {
        if (!_requestSessions.TryGetValue(requestId, out var session))
            session = GetReadySession();
        try
        {
            await foreach (var item in stream.WithCancellation(cancellationToken))
            {
                await session.SendStreamChunkAsync(
                    requestId,
                    streamId,
                    item,
                    cancellationToken).ConfigureAwait(false);
            }

            session.SendStreamCompleteAsync(requestId, streamId);
        }
        catch (Exception ex)
        {
            try
            {
                session.SendStreamErrorAsync(requestId, streamId, ex);
            }
            catch (SharpLinkException sendException) when (sendException.Code == SharpLinkErrorCode.ConnectionClosed)
            {
            }
            throw;
        }
    }

    private async Task RunStreamSenderAsync(Func<long, CancellationToken, Task> streamSender, long requestId, CancellationToken ct)
    {
        try
        {
            await streamSender(requestId, ct);
        }
        catch (Exception ex)
        {
            TryUnbindRequest(requestId, out _);
            _requestManager.DispatchError(requestId, ex);
        }
    }

    private static ValueTask DispatchStreamChunkAsync(IRpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short streamIdBits))
            throw CreateProtocolViolationException("StreamData stream ID is truncated.");
        var streamId = unchecked((ushort)streamIdBits);
        var streamPayload = payload.Slice(sizeof(ushort));
        return session.StreamManager.DispatchChunkAsync(requestId, streamId, streamPayload);
    }

    private void DispatchStreamComplete(
        IRpcSession session,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        SharpLinkProtocolOptions limits)
    {
        var streamId = TryReadStreamId(ref payload);
        if ((flags & ProtocolV2FrameFlags.Error) == 0)
        {
            session.StreamManager.CompleteStream(requestId, streamId, exception: null);
            if (streamId == 0)
            {
                _serverStreamRequestIds.Remove(requestId);
                TryUnbindRequest(requestId, out _);
                CompleteStreamLifetime(requestId);
            }
            return;
        }
        var error = ProtocolV2PayloadCodec.ReadError(payload, flags, limits.MaxErrorMessageBytes);
        session.StreamManager.CompleteStream(
            requestId,
            streamId,
            new SharpLinkException(error.Code, error.Message));
        if (streamId == 0)
        {
            _serverStreamRequestIds.Remove(requestId);
            TryUnbindRequest(requestId, out _);
            CompleteStreamLifetime(requestId);
        }
    }

    private static ushort TryReadStreamId(ref ReadOnlySequence<byte> payload)
    {
        var firstSpan = payload.FirstSpan;
        ushort streamId;
        if (firstSpan.Length >= sizeof(ushort))
        {
            streamId = BinaryPrimitives.ReadUInt16LittleEndian(firstSpan);
        }
        else
        {
            var reader = new SequenceReader<byte>(payload);
            if (!reader.TryReadLittleEndian(out short streamIdBits))
                throw CreateProtocolViolationException("StreamComplete stream ID is truncated.");
            streamId = unchecked((ushort)streamIdBits);
        }

        payload = payload.Slice(sizeof(ushort));
        return streamId;
    }

    private void HandleDisconnected(RpcSession session, Exception ex)
    {
        if (!RemoveReadySession(session, out var sessionCts))
            return;

        using var sessionScope = BeginSessionLogScope(_logger, session.Id);
        LogClientDisconnectedWithError(_logger, ex);

        if (sessionCts is not null)
        {
            sessionCts.Cancel();
            sessionCts.Dispose();
        }
        FailRequestsForSession(session, ex);
        session.StreamManager.CompleteAll(ex);
        TrackBackgroundTask(DisposeDisconnectedSessionAsync(session));

        if (_shutdownCts.IsCancellationRequested ||
            State is SharpLinkConnectionState.Stopped)
            return;

        if (ReadyConnectionCount != 0)
        {
            TransitionTo(SharpLinkConnectionState.Ready);
            EnsureReconnectLoop();
            return;
        }

        ResetReadySignal();
        var stableTicks = Stopwatch.GetTimestamp() - Volatile.Read(ref _readyTimestamp);
        if (stableTicks >= 30L * Stopwatch.Frequency)
            Volatile.Write(ref _reconnectDelayMilliseconds, 100);
        TransitionTo(SharpLinkConnectionState.Reconnecting);
        EnsureReconnectLoop();
    }

    private void EnsureReconnectLoop()
    {
        lock (_stateGate)
        {
            if (_shutdownCts.IsCancellationRequested || _reconnectTask is { IsCompleted: false })
                return;
            _reconnectTask = ReconnectLoopAsync();
        }
    }

    private async Task ReconnectLoopAsync()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            if (ReadyConnectionCount >= _connectionPoolOptions.MinConnections)
                return;
            var baseDelay = Volatile.Read(ref _reconnectDelayMilliseconds);
            var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
            var delay = TimeSpan.FromMilliseconds(baseDelay * jitter);
            try
            {
                await Task.Delay(delay, _shutdownCts.Token).ConfigureAwait(false);
                SharpLinkTelemetry.ReconnectAttempt();
                await ConnectOneAsync(_shutdownCts.Token).ConfigureAwait(false);
                PublishReadyState();
                if (ReadyConnectionCount >= _connectionPoolOptions.MinConnections)
                    return;
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                using var scope = BeginSessionLogScope(_logger, "reconnect");
                LogClientBackgroundLoopUnhandledException(_logger, nameof(ReconnectLoopAsync), ex);
                var nextDelay = Math.Min(baseDelay * 2, 5000);
                Volatile.Write(ref _reconnectDelayMilliseconds, nextDelay);
                TransitionTo(SharpLinkConnectionState.Reconnecting);
            }
        }
    }

    private RpcSession GetReadySession()
    {
        var sessions = Volatile.Read(ref _readySessions);
        if (State == SharpLinkConnectionState.Ready && sessions.Length != 0)
        {
            RpcSession selected;
            if (sessions.Length == 1)
            {
                selected = sessions[0];
            }
            else
            {
                var first = Random.Shared.Next(sessions.Length);
                var second = Random.Shared.Next(sessions.Length - 1);
                if (second >= first)
                    second++;
                selected = SelectLeastLoaded(sessions, first, second);
            }

            if (selected.CanAcceptCalls)
            {
                if (selected.ActiveRequestCount != 0)
                    EnsureExpansion();
                return selected;
            }
        }
        if (State is SharpLinkConnectionState.Draining or SharpLinkConnectionState.Stopped)
            throw CreateConnectionClosedException("Client is not accepting new calls.");
        throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "No SharpLink connection is ready.");
    }

    internal static RpcSession SelectLeastLoaded(RpcSession[] sessions, int first, int second)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegative(second);
        if ((uint)first >= (uint)sessions.Length || (uint)second >= (uint)sessions.Length)
            throw new ArgumentOutOfRangeException(nameof(first));
        var firstSession = sessions[first];
        var secondSession = sessions[second];
        return firstSession.ActiveRequestCount <= secondSession.ActiveRequestCount
            ? firstSession
            : secondSession;
    }

    private bool RemoveReadySession(
        RpcSession session,
        out CancellationTokenSource? sessionCancellation)
    {
        lock (_poolGate)
        {
            if (!_sessionCancellations.Remove(session, out sessionCancellation))
                return false;
            PublishReadySnapshotLocked();
            return true;
        }
    }

    private void MarkSessionDraining(RpcSession session)
    {
        session.MarkDraining();
        lock (_poolGate)
            PublishReadySnapshotLocked();

        RetireDrainingSessionIfIdle(session);

        if (ReadyConnectionCount != 0)
        {
            EnsureReconnectLoop();
            return;
        }
        ResetReadySignal();
        TransitionTo(SharpLinkConnectionState.Draining);
        EnsureReconnectLoop();
    }

    private void RetireDrainingSessionIfIdle(RpcSession session)
    {
        if (!session.IsDraining || session.ActiveRequestCount != 0 ||
            !RemoveReadySession(session, out var sessionCancellation))
        {
            return;
        }

        sessionCancellation?.Cancel();
        sessionCancellation?.Dispose();
        TrackBackgroundTask(DisposeDisconnectedSessionAsync(session));
    }

    private void EnsureExpansion()
    {
        if (ReadyConnectionCount >= _connectionPoolOptions.MaxConnections)
            return;

        lock (_stateGate)
        {
            if (_shutdownCts.IsCancellationRequested ||
                ReadyConnectionCount >= _connectionPoolOptions.MaxConnections ||
                _expansionTask is { IsCompleted: false })
            {
                return;
            }
            _expansionTask = ExpandOneAsync();
        }
    }

    private async Task ExpandOneAsync()
    {
        try
        {
            if (ReadyConnectionCount >= _connectionPoolOptions.MaxConnections)
                return;
            await ConnectOneAsync(_shutdownCts.Token).ConfigureAwait(false);
            PublishReadyState();
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            using var scope = BeginSessionLogScope(_logger, "pool-expand");
            LogClientBackgroundLoopUnhandledException(_logger, nameof(ExpandOneAsync), ex);
        }
    }

    private void FailRequestsForSession(RpcSession session, Exception exception)
    {
        foreach (var pair in _requestSessions)
        {
            if (!ReferenceEquals(pair.Value, session) || !TryUnbindRequest(pair.Key, out _))
                continue;
            _requestManager.DispatchError(pair.Key, exception);
            _serverStreamRequestIds.Remove(pair.Key);
            _locallyCanceledRequestIds.Remove(pair.Key);
            CompleteStreamLifetime(pair.Key);
        }
    }

    private void OnRequestCancel(RequestCancelState state)
    {
        if (!state.IsOneWay && !_locallyCanceledRequestIds.Add(state.RequestId))
            return;

        TrySendCancel(state.RequestId);

        if (state.IsOneWay) return;
        var ex = CreateCancellationException(state.UserToken);
        _requestManager.DispatchError(state.RequestId, ex);
        TryUnbindRequest(state.RequestId, out _);
    }

    private void OnStreamCancel(StreamCancelState state)
    {
        if (!_locallyCanceledRequestIds.Add(state.RequestId))
            return;

        TrySendCancel(state.RequestId);
        var ex = CreateCancellationException(state.UserToken);
        if (_requestSessions.TryGetValue(state.RequestId, out var session))
            session.StreamManager.CompleteStream(state.RequestId, ex);
        _serverStreamRequestIds.Remove(state.RequestId);
        TryUnbindRequest(state.RequestId, out _);
    }

    private void OnRequestTimeout(RequestTimeoutState state)
    {
        if (!state.IsOneWay && !_locallyCanceledRequestIds.Add(state.RequestId))
            return;

        TrySendCancel(state.RequestId);
        if (!state.IsOneWay)
        {
            _requestManager.DispatchError(state.RequestId, new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded."));
            TryUnbindRequest(state.RequestId, out _);
        }
    }

    private void OnStreamTimeout(StreamTimeoutState state)
    {
        if (!_locallyCanceledRequestIds.Add(state.RequestId))
            return;

        TrySendCancel(state.RequestId);
        if (_requestSessions.TryGetValue(state.RequestId, out var session))
        {
            session.StreamManager.CompleteStream(state.RequestId, new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded."));
        }
        _serverStreamRequestIds.Remove(state.RequestId);
        TryUnbindRequest(state.RequestId, out _);
    }

    private void TrySendCancel(long requestId)
    {
        try
        {
            if (_requestSessions.TryGetValue(requestId, out var requestSession))
                requestSession.SendCancelAsync(requestId);
            else
                _session?.SendCancelAsync(requestId);
        }
        catch (SharpLinkException ex) when (ex.Code is
            SharpLinkErrorCode.ConnectionClosed or
            SharpLinkErrorCode.ResourceExhausted or
            SharpLinkErrorCode.Unavailable)
        {
        }
    }

    private static async Task DisposeDisconnectedSessionAsync(IRpcSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or SocketException)
        {
        }
    }

    private TimeoutRegistration RegisterRequestTimeout(DateTimeOffset? deadline, long requestId, bool isOneWay)
    {
        if (deadline is not { } absoluteDeadline)
            return default;

        var state = RequestTimeoutState.Rent(this, requestId, isOneWay);
        return _requestTimeoutScheduler.Schedule(requestId, absoluteDeadline, SRequestTimeoutCallback, state);
    }

    private TimeoutRegistration RegisterStreamTimeout(DateTimeOffset? deadline, long requestId)
    {
        if (deadline is not { } absoluteDeadline)
            return default;

        var state = StreamTimeoutState.Rent(this, requestId);
        return _requestTimeoutScheduler.Schedule(requestId, absoluteDeadline, SStreamTimeoutCallback, state);
    }

}
