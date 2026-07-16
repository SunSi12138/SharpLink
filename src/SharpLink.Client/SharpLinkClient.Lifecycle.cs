namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        Task connectTask;
        lock (_stateGate)
        {
            if (State == SharpLinkConnectionState.Ready)
                return ValueTask.CompletedTask;
            if (State is SharpLinkConnectionState.Draining or SharpLinkConnectionState.Stopped ||
                _shutdownCts.IsCancellationRequested)
            {
                return ValueTask.FromException(CreateConnectionClosedException("Client has stopped."));
            }

            if (_connectTask is not { IsCompleted: false })
            {
                TransitionTo(SharpLinkConnectionState.Connecting);
                _connectTask = ConnectInitialAsync(cancellationToken);
            }
            connectTask = _connectTask;
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask(connectTask.WaitAsync(cancellationToken))
            : new ValueTask(connectTask);
    }

    private async Task ConnectInitialAsync(CancellationToken cancellationToken)
    {
        var connected = new List<RpcSession>(_connectionPoolOptions.MinConnections);
        try
        {
            for (var index = 0; index < _connectionPoolOptions.MinConnections; index++)
                connected.Add(await ConnectOneAsync(cancellationToken).ConfigureAwait(false));
            PublishReadyState();
        }
        catch
        {
            for (var index = 0; index < connected.Count; index++)
            {
                RemoveReadySession(connected[index], out var cancellation);
                cancellation?.Cancel();
                cancellation?.Dispose();
                await connected[index].DisposeAsync().ConfigureAwait(false);
            }
            if (!_shutdownCts.IsCancellationRequested)
                TransitionTo(SharpLinkConnectionState.Faulted);
            throw;
        }
    }

    private async Task<RpcSession> ConnectOneAsync(CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCts.Token);
        RpcSession? session = null;
        ITransportConnection? connection = null;
        try
        {
            connection = await transportFactory.ConnectAsync(attemptCts.Token).ConfigureAwait(false);
            if (connection is ITransportSecurityInfo securityInfo)
                LogTlsEstablished(_logger, securityInfo.Protocol, securityInfo.CipherSuite);
            session = new RpcSession(connection, _rpcSessionFlushOptions);
            connection = null;
            session.SetTelemetrySide("client");
            session.BindRuntimeContext(_runtimeContext);

            using var handshakeTimeoutCts = new CancellationTokenSource(_protocolOptions.HandshakeTimeout);
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(
                attemptCts.Token,
                handshakeTimeoutCts.Token);
            Exception? handshakeException;
            try
            {
                handshakeException = await ProcessHandshakeAsync(session, handshakeCts.Token);
            }
            catch (OperationCanceledException) when (handshakeCts.IsCancellationRequested)
            {
                handshakeException = new OperationCanceledException(handshakeCts.Token);
            }
            if (handshakeException is OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                if (handshakeTimeoutCts.IsCancellationRequested)
                {
                    throw new SharpLinkException(
                        SharpLinkErrorCode.Unavailable,
                        $"RPC handshake timed out after {_protocolOptions.HandshakeTimeout}.");
                }
                throw new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Client stopped during handshake.");
            }

            if (handshakeException is not null)
                throw handshakeException;

            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            var readySession = session;
            readySession.OnDisconnected += exception => HandleDisconnected(
                readySession,
                exception ?? CreateConnectionClosedException("Transport closed."));

            lock (_poolGate)
            {
                if (_shutdownCts.IsCancellationRequested ||
                    State is SharpLinkConnectionState.Stopped)
                {
                    sessionCts.Dispose();
                    throw CreateConnectionClosedException("Client stopped while connecting.");
                }
                if (CountReadyConnectionsLocked() >= _connectionPoolOptions.MaxConnections)
                {
                    sessionCts.Dispose();
                    throw new InvalidOperationException("The connection pool is already at capacity.");
                }

                _sessionCancellations.Add(readySession, sessionCts);
                PublishReadySnapshotLocked();
            }

            readySession.NotifyConnected();
            TrackBackgroundTask(RunHeartbeatSendLoopAsync(readySession, sessionCts.Token));
            TrackBackgroundTask(RunProcessRequestLoopAsync(readySession, sessionCts.Token));
            session = null;
            return readySession;
        }
        catch
        {
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void PublishReadyState()
    {
        lock (_stateGate)
        {
            if (_shutdownCts.IsCancellationRequested)
                return;
            _readyTimestamp = Stopwatch.GetTimestamp();
            TransitionTo(SharpLinkConnectionState.Ready);
            _readySignal.TrySetResult(true);
        }
    }

    private void PublishReadySnapshotLocked()
    {
        var snapshot = new RpcSession[_sessionCancellations.Count];
        var index = 0;
        foreach (var candidate in _sessionCancellations.Keys)
        {
            if (candidate.CanAcceptCalls)
                snapshot[index++] = candidate;
        }
        if (index != snapshot.Length)
            Array.Resize(ref snapshot, index);
        Volatile.Write(ref _readySessions, snapshot);
        Volatile.Write(ref _session, snapshot.Length == 0 ? null : snapshot[0]);
    }

    private int CountReadyConnectionsLocked()
    {
        var count = 0;
        foreach (var candidate in _sessionCancellations.Keys)
        {
            if (candidate.CanAcceptCalls)
                count++;
        }
        return count;
    }

    private static bool IsExpectedCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;

    private static bool IsTransportFault(Exception ex)
        => ex is IOException or ObjectDisposedException or SocketException;

    private async Task RunHeartbeatSendLoopAsync(RpcSession session, CancellationToken ct)
    {
        try
        {
            await HeartbeatSendLoop(session, ct);
        }
        catch (Exception ex) when (IsExpectedCancellation(ex, ct))
        {
        }
        catch (Exception ex)
        {
            using var sessionScope = BeginSessionLogScope(_logger, session.Id);
            LogClientBackgroundLoopUnhandledException(_logger, nameof(HeartbeatSendLoop), ex);
            HandleDisconnected(session, IsTransportFault(ex)
                ? CreateConnectionClosedException("Transport closed.", ex)
                : ex);
        }
    }

    private async Task RunProcessRequestLoopAsync(RpcSession session, CancellationToken ct)
    {
        try
        {
            await ProcessRequestLoop(session, ct);
        }
        catch (Exception ex) when (IsExpectedCancellation(ex, ct))
        {
        }
        catch (Exception ex)
        {
            if (ex is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation })
                SharpLinkTelemetry.RecordProtocolFailure("client");
            using var sessionScope = BeginSessionLogScope(_logger, session.Id);
            LogClientBackgroundLoopUnhandledException(_logger, nameof(ProcessRequestLoop), ex);
            HandleDisconnected(session, IsTransportFault(ex)
                ? CreateConnectionClosedException("Transport closed.", ex)
                : ex);
        }
    }

    public T Get<T>() where T : IService
    {
        if (GeneratedProxyRegistry.TryCreate(typeof(T), this, out var proxy))
            return (T)proxy!;

        throw new InvalidOperationException($"Proxy for service interface {typeof(T).FullName} is not registered.");
    }

    private async Task<Exception?> ProcessHandshakeAsync(IRpcSession session, CancellationToken ct)
    {
        var authPayload = _authenticator is null
            ? ReadOnlyMemory<byte>.Empty
            : await _authenticator.CreatePayloadAsync(ct).ConfigureAwait(false);
        if (authPayload.Length > _protocolOptions.MaxMetadataBytes)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                $"Authentication payload exceeds {_protocolOptions.MaxMetadataBytes} bytes.");
        }
        var handshakeRequest = new ProtocolV2HandshakeRequest(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.Metadata | ProtocolV2Capabilities.FlowControl,
            ProtocolV2Capabilities.None,
            _protocolOptions.MaxFramePayloadBytes,
            _runtimeContext.FlowControl.StreamReceiveWindowBytes,
            _runtimeContext.FlowControl.ConnectionReceiveWindowBytes,
            authPayload);
        await session.SendHandshakeRequestAndFlushAsync(handshakeRequest, _protocolOptions, ct).ConfigureAwait(false);

        var reader = session.Input;
        Exception? handshakeException = null;
        var handshakeCompleted = false;
        while (session.IsConnected && !ct.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            while (ProtocolV2FrameParser.TryReadFrame(ref buffer, _protocolOptions, out var header, out var payload))
            {
                SharpLinkTelemetry.RecordReceivedBytes(ProtocolV2Constants.HeaderBytes + payload.Length);
                if (header.Type != ProtocolV2FrameType.HandshakeResponse)
                    handshakeException = CreateProtocolViolationException("Received unexpected packet during handshake.");
                else if ((header.Flags & ProtocolV2FrameFlags.Error) == 0)
                {
                    var response = ProtocolV2PayloadCodec.ReadHandshakeResponse(payload, _protocolOptions);
                    if (response.MinorVersion > ProtocolV2Constants.MinorVersion)
                    {
                        handshakeException = new SharpLinkException(SharpLinkErrorCode.Unimplemented,
                            $"Server requires unsupported protocol minor version {response.MinorVersion}.");
                    }
                    else
                    {
                        ((RpcSession)session).NegotiatedCapabilities = response.NegotiatedCapabilities;
                        if ((response.NegotiatedCapabilities & ProtocolV2Capabilities.FlowControl) != 0)
                        {
                            ((RpcSession)session).EnableStreamFlowControl(
                                response.StreamReceiveWindowBytes,
                                response.ConnectionReceiveWindowBytes);
                        }
                        handshakeException = null;
                    }
                }
                else
                {
                    var error = ProtocolV2PayloadCodec.ReadError(
                        payload, header.Flags, _protocolOptions.MaxErrorMessageBytes);
                    handshakeException = new SharpLinkException(error.Code, error.Message);
                    if (error.Code is SharpLinkErrorCode.AuthenticationRejected or
                        SharpLinkErrorCode.AuthenticationExpired or
                        SharpLinkErrorCode.AuthorizationDenied or
                        SharpLinkErrorCode.PermissionDenied)
                    {
                        SharpLinkTelemetry.RecordAuthenticationFailure("client");
                    }
                }

                handshakeCompleted = true;
                break;
            }
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (handshakeCompleted)
            {
                if (handshakeException is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation })
                    SharpLinkTelemetry.RecordProtocolFailure("client");
                return handshakeException;
            }

            if (result.IsCompleted)
                break;
        }

        return ct.IsCancellationRequested
            ? new OperationCanceledException(ct)
            : CreateConnectionClosedException("Server disconnected during handshake.");
    }

    private async Task ProcessRequestLoop(RpcSession session, CancellationToken ct)
    {
        var reader = session.Input;
        using var sessionScope = BeginSessionLogScope(_logger, session.Id);

        while (session.IsConnected && !ct.IsCancellationRequested)
        {
            ReadResult result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            try
            {
                while (ProtocolV2FrameParser.TryReadFrame(ref buffer, _protocolOptions, out var header, out var payload))
                {
                    SharpLinkTelemetry.RecordReceivedBytes(ProtocolV2Constants.HeaderBytes + payload.Length);
                    session.LastActive = DateTime.UtcNow;
                    switch (header.Type)
                    { 
                        case ProtocolV2FrameType.Ping:
                            session.SendPongAsync(ReadMonotonicTimestamp(payload));
                            break;
                        case ProtocolV2FrameType.Pong:
                            DebugLogServerHeartbeatReceived(_logger);
                            break;
                        case ProtocolV2FrameType.Cancel:
                            DebugLogServerCancelIgnored(_logger);
                            break;
                        case ProtocolV2FrameType.Response:
                            DispatchRpc(unchecked((long)header.RequestId), header.Flags, ref payload);
                            break;
                        case ProtocolV2FrameType.StreamData:
                            var dispatchTask = DispatchStreamChunkAsync(session, unchecked((long)header.RequestId), payload);
                            if (!dispatchTask.IsCompletedSuccessfully)
                                await dispatchTask;
                            break;
                        case ProtocolV2FrameType.StreamComplete:
                            DispatchStreamComplete(
                                session, unchecked((long)header.RequestId), header.Flags, payload, _protocolOptions);
                            break;
                        case ProtocolV2FrameType.WindowUpdate:
                            session.ApplyWindowUpdate(
                                unchecked((long)header.RequestId),
                                ProtocolV2PayloadCodec.ReadWindowUpdate(payload));
                            break;
                        case ProtocolV2FrameType.GoAway:
                            if (payload.Length < sizeof(ulong))
                                throw CreateProtocolViolationException("GoAway last accepted request ID is truncated.");
                            var goAwayError = ProtocolV2PayloadCodec.ReadError(
                                payload.Slice(sizeof(ulong)),
                                header.Flags | ProtocolV2FrameFlags.Error,
                                _protocolOptions.MaxErrorMessageBytes);
                            MarkSessionDraining(session);
                            using (BeginRequestLogScope(_logger, unchecked((long)header.RequestId)))
                                LogClientDisconnectedWithError(
                                    _logger,
                                    new SharpLinkException(goAwayError.Code, goAwayError.Message));
                            break;
                        case ProtocolV2FrameType.HandshakeRequest:
                        case ProtocolV2FrameType.HandshakeResponse:
                        case ProtocolV2FrameType.Request:
                        default:
                            SharpLinkTelemetry.RecordProtocolFailure("client");
                            await session.DisposeAsync();
                            HandleDisconnected(session, CreateProtocolViolationException("Received unexpected packet from server."));
                            return;
                    }
                }

                if (result.IsCompleted)
                    break;
            }
            finally
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }

        HandleDisconnected(session, CreateConnectionClosedException("Server disconnected."));
    }

    private async Task HeartbeatSendLoop(RpcSession session, CancellationToken ct)
    {
        using var sessionScope = BeginSessionLogScope(_logger, session.Id);
        while (!ct.IsCancellationRequested)
        {
            session.SendPingAsync();
            await Task.Delay(_heartbeatInterval, ct);
            var now = DateTime.UtcNow;
            if (now - session.LastActive <= _heartbeatTimeout && session.IsConnected)
                continue;

            LogServerHeartbeatTimeout(_logger);

            await session.DisposeAsync();
            HandleDisconnected(session, CreateHeartbeatTimeoutException("Server heartbeat timeout."));
            break;
        }
    }

    private void DispatchRpc(long requestId, ProtocolV2FrameFlags flags, ref ReadOnlySequence<byte> payload)
    {
        var isError = (flags & ProtocolV2FrameFlags.Error) != 0;

        if (isError)
        {
            var error = ProtocolV2PayloadCodec.ReadError(payload, flags, _protocolOptions.MaxErrorMessageBytes);
            var remoteException = new SharpLinkException(error.Code, error.Message);
            if (_requestManager.DispatchError(requestId, remoteException))
            {
                TryUnbindRequest(requestId, out _);
                return;
            }

            if (_serverStreamRequestIds.Remove(requestId))
            {
                if (TryUnbindRequest(requestId, out var requestSession))
                    requestSession.StreamManager.CompleteStream(requestId, remoteException);
                CompleteStreamLifetime(requestId);
                return;
            }
        }
        else
        {
            if (_requestManager.Dispatch(requestId, ref payload))
            {
                TryUnbindRequest(requestId, out _);
                return;
            }

            // Server-stream receives a terminal RpcResponse ACK; swallow it.
            if (_serverStreamRequestIds.Contains(requestId))
            {
                return;
            }
        }

        if (_locallyCanceledRequestIds.Remove(requestId))
            return;

        using var requestScope = BeginRequestLogScope(_logger, requestId);
        LogUnknownOrTimedOutResponse(_logger);
    }

    private static long ReadMonotonicTimestamp(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long timestamp))
            throw CreateProtocolViolationException("Heartbeat timestamp is truncated.");
        return timestamp;
    }
}
