using System.Diagnostics;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    public ValueTask RunAsync(CancellationToken cancellationToken = default)
    {
        Task runTask;
        lock (_stateGate)
        {
            if (_runTask is null)
            {
                if (CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
                    return ValueTask.FromException(new SharpLinkException(
                        SharpLinkErrorCode.ConnectionClosed,
                        "Server cannot be restarted."));
                _runTask = RunCoreAsync(cancellationToken);
            }
            runTask = _runTask;
        }
        return new ValueTask(runTask);
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        TransitionTo(ServerState.Starting);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _acceptCts.Token);
        var acceptToken = runCts.Token;
        TransitionTo(ServerState.Running);
        TrackFrameworkTask(RunHeartbeatCheckLoopAsync(_forceStopCts.Token));

        try
        {
            while (!acceptToken.IsCancellationRequested)
            {
                ITransportConnection? connection = null;
                try
                {
                    connection = await transportListener.AcceptAsync(acceptToken).ConfigureAwait(false);
                    TrackFrameworkTask(HandleAcceptedConnectionAsync(connection, _forceStopCts.Token));
                    connection = null;
                }
                catch (OperationCanceledException) when (acceptToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (acceptToken.IsCancellationRequested || CurrentState == ServerState.Draining)
                {
                    break;
                }
                catch
                {
                    if (connection is not null)
                        await connection.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            if (cancellationToken.IsCancellationRequested && CurrentState == ServerState.Running)
            {
                Task stopTask;
                lock (_stateGate)
                {
                    _stopTask ??= StopCoreAsync(TimeSpan.Zero);
                    stopTask = _stopTask;
                }
                await stopTask.ConfigureAwait(false);
            }
            else if (CurrentState == ServerState.Draining)
            {
                Task? stopTask;
                lock (_stateGate)
                    stopTask = _stopTask;
                if (stopTask is not null)
                    await stopTask.ConfigureAwait(false);
            }
        }
        catch
        {
            TransitionTo(ServerState.Faulted);
            Task cleanupTask;
            lock (_stateGate)
            {
                _stopTask ??= CleanupAfterRunFailureAsync();
                cleanupTask = _stopTask;
            }
            await cleanupTask.ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsExpectedCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;

    private async Task HandleAcceptedConnectionAsync(
        ITransportConnection acceptedConnection,
        CancellationToken cancellationToken)
    {
        ITransportConnection? connection = acceptedConnection;
        ServerConnectionState? connectionState = null;
        try
        {
            if (connection is ITransportSecurityHandshake securityHandshake)
            {
                try
                {
                    await securityHandshake.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsExpectedCancellation(exception, cancellationToken))
                {
                    return;
                }
                catch (Exception exception) when (
                    exception is AuthenticationException or System.IO.IOException or SocketException or SharpLinkException)
                {
                    LogTlsHandshakeFailed(_logger, exception);
                    return;
                }
            }
            if (connection is ITransportSecurityInfo securityInfo)
                LogTlsEstablished(_logger, securityInfo.Protocol, securityInfo.CipherSuite);

            var session = new RpcSession(connection, _rpcSessionFlushOptions);
            connectionState = new ServerConnectionState(
                session,
                _runtimeContext.Concurrency,
                cancellationToken,
                _maxConcurrentCallsPerConnection);
            connection = null;
            session.SetTelemetrySide("server");
            session.BindRuntimeContext(_runtimeContext);
            session.ServiceExceptionMapper = (requestId, contractId, methodId, exception) =>
                MapStreamServiceException(session, requestId, contractId, methodId, exception);
            await ReplaceConnectionAsync(connectionState).ConfigureAwait(false);
            await HandleSessionLifecycleAsync(connectionState).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedCancellation(exception, cancellationToken))
        {
        }
        finally
        {
            if (connectionState is not null)
                await connectionState.CloseAsync().ConfigureAwait(false);
            else if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunHeartbeatCheckLoopAsync(CancellationToken ct)
    {
        try
        {
            await HeartbeatCheckLoop(ct);
        }
        catch (Exception ex) when (IsExpectedCancellation(ex, ct))
        {
        }
        catch (Exception ex)
        {
            LogServerBackgroundLoopUnhandledException(_logger, nameof(HeartbeatCheckLoop), ex);
        }
    }

    private async Task HandleSessionLifecycleAsync(ServerConnectionState connection)
    {
        var session = connection.Session;
        var ct = connection.ConnectionToken;
        var hasConnected = false;
        using var sessionScope = BeginSessionLogScope(_logger, session.Id);
        try
        {
            using var handshakeTimeoutCts = new CancellationTokenSource(_protocolOptions.HandshakeTimeout);
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, handshakeTimeoutCts.Token);
            SharpLinkAuthenticationResult authResult;
            try
            {
                authResult = await ProcessHandshakeAsync(session, handshakeCts.Token);
            }
            catch (OperationCanceledException) when (handshakeTimeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                var timeoutException = new SharpLinkException(
                    SharpLinkErrorCode.Unavailable,
                    $"RPC handshake timed out after {_protocolOptions.HandshakeTimeout}.");
                session.NotifyDisconnected(timeoutException);
                LogHandshakeFailed(_logger);
                return;
            }
            if (!authResult.IsAuthenticated)
            {
                LogHandshakeFailed(_logger);
                return;
            }

            if (CurrentState != ServerState.Running)
            {
                connection.MarkDraining();
                return;
            }
            
            if (!connection.MarkReady(authResult.Context))
                return;

            hasConnected = true;
            session.NotifyConnected();
            LogClientConnected(_logger);
            await ProcessRequestLoop(connection);
        }
        catch (Exception ex) when (IsExpectedCancellation(ex, ct))
        {
        }
        catch (Exception ex)
        {
            if (ex is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation })
                SharpLinkTelemetry.RecordProtocolFailure("server");
            LogServerBackgroundLoopUnhandledException(_logger, nameof(ProcessRequestLoop), ex);
        }
        finally
        {
            if (hasConnected)
                LogClientDisconnected(_logger);
            await DisconnectConnectionAsync(connection).ConfigureAwait(false);
        }
    }

    private async ValueTask ReplaceConnectionAsync(ServerConnectionState connection)
    {
        var id = connection.Session.Id;
        while (true)
        {
            if (_connections.TryAdd(id, connection))
                return;

            if (!_connections.TryGetValue(id, out var previous))
                continue;
            if (!_connections.TryUpdate(id, connection, previous))
                continue;

            await RetireConnectionAsync(previous).ConfigureAwait(false);
            return;
        }
    }

    private async ValueTask DisconnectConnectionAsync(ServerConnectionState connection)
    {
        connection.MarkDraining();
        var added = _retiredConnections.TryAdd(connection, 0);
        _connections.TryRemove(
            new KeyValuePair<string, ServerConnectionState>(connection.Session.Id, connection));
        try
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            if (added)
                _ = CompleteRetiredConnectionCleanupAsync(connection);
        }
    }

    private async ValueTask RetireConnectionAsync(ServerConnectionState connection)
    {
        connection.MarkDraining();
        var added = _retiredConnections.TryAdd(connection, 0);
        try
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            if (added)
                _ = CompleteRetiredConnectionCleanupAsync(connection);
        }
    }

    private async Task CompleteRetiredConnectionCleanupAsync(ServerConnectionState connection)
    {
        try
        {
            await connection.ServiceCleanupTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDeferredCleanupFailed(_logger, "ConnectionServices", exception);
        }
        finally
        {
            _retiredConnections.TryRemove(connection, out _);
        }
    }

    private async Task HeartbeatCheckLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(heartbeatCheckInterval,ct);
            var now = DateTime.UtcNow;
            foreach (var (id, connection) in _connections)
            {
                var session = connection.Session;
                if (now - session.LastActive <= heartbeatTimeout || !session.IsConnected)
                    continue;
                
                using var sessionScope = BeginSessionLogScope(_logger, session.Id);
                LogClientHeartbeatTimeout(_logger);
                
                if (_connections.TryGetValue(id, out var current) && ReferenceEquals(current, connection))
                    await DisconnectConnectionAsync(connection).ConfigureAwait(false);
            }
        }
    }
    private async Task<SharpLinkAuthenticationResult> ProcessHandshakeAsync(IRpcSession session, CancellationToken ct)
    {
        
        var reader = session.Input;
        SharpLinkAuthenticationResult? handshakeResult = null;

        while (session.IsConnected && !ct.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(ct);
            var buffer =  result.Buffer;
            while (ProtocolV2FrameParser.TryReadFrame(ref buffer, _protocolOptions, out var header, out var message))
            {
                SharpLinkTelemetry.RecordReceivedBytes(ProtocolV2Constants.HeaderBytes + message.Length);
                SharpLinkAuthenticationResult authResult;
                ProtocolV2HandshakeRequest request = default;
                var supportedCapabilities =
                    ProtocolV2Capabilities.Metadata |
                    ProtocolV2Capabilities.FlowControl |
                    ProtocolV2Capabilities.HealthCheck |
                    ProtocolV2Capabilities.CancellationReason;
                if (_runtimeContext.Compression.Providers.Count != 0)
                    supportedCapabilities |= ProtocolV2Capabilities.Compression;
                if (header.Type != ProtocolV2FrameType.HandshakeRequest)
                {
                    authResult = SharpLinkAuthenticationResult.Reject(
                        SharpLinkErrorCode.ProtocolViolation,
                        "Expected HandshakeRequest frame.");
                }
                else
                {
                    request = ProtocolV2PayloadCodec.ReadHandshakeRequest(message, _protocolOptions);
                    var unsupportedRequired = request.RequiredCapabilities & ~supportedCapabilities;
                    if (unsupportedRequired != ProtocolV2Capabilities.None)
                    {
                        authResult = SharpLinkAuthenticationResult.Reject(
                            SharpLinkErrorCode.Unimplemented,
                            $"Required capabilities are unsupported: {unsupportedRequired}.");
                    }
                    else if ((request.RequiredCapabilities & ProtocolV2Capabilities.Compression) != 0 &&
                             SelectCompressionProvider(request) is null)
                    {
                        authResult = SharpLinkAuthenticationResult.Reject(
                            SharpLinkErrorCode.Unimplemented,
                            "Required compression has no mutually supported profile.");
                    }
                    else
                    {
                        authResult = await AuthenticateAsync(session, request.AuthenticationPayload, ct)
                            .ConfigureAwait(false);
                    }
                }

                if (authResult.IsAuthenticated)
                {
                    var compressionProvider = SelectCompressionProvider(request);
                    var negotiatedCapabilities = request.SupportedCapabilities & supportedCapabilities;
                    if (compressionProvider is null)
                        negotiatedCapabilities &= ~ProtocolV2Capabilities.Compression;
                    var response = new ProtocolV2HandshakeResponse(
                        Math.Min(request.MinorVersion, ProtocolV2Constants.MinorVersion),
                        negotiatedCapabilities,
                        Math.Min(request.MaxFramePayloadBytes, _protocolOptions.MaxFramePayloadBytes),
                        Math.Min(request.StreamReceiveWindowBytes, _runtimeContext.FlowControl.StreamReceiveWindowBytes),
                        Math.Min(request.ConnectionReceiveWindowBytes, _runtimeContext.FlowControl.ConnectionReceiveWindowBytes),
                        compressionProvider?.WireProfile);
                    var runtimeSession = (RpcSession)session;
                    runtimeSession.NegotiatedCapabilities = response.NegotiatedCapabilities;
                    runtimeSession.SetNegotiatedMaxFramePayloadBytes(response.MaxFramePayloadBytes);
                    if (compressionProvider is not null)
                        runtimeSession.EnableCompression(compressionProvider);
                    if ((response.NegotiatedCapabilities & ProtocolV2Capabilities.FlowControl) != 0)
                    {
                        runtimeSession.EnableStreamFlowControl(
                            response.StreamReceiveWindowBytes,
                            response.ConnectionReceiveWindowBytes);
                    }
                    await session.SendHandshakeResponseAndFlushAsync(response, ct).ConfigureAwait(false);
                }
                else
                {
                    if (authResult.ErrorCode == SharpLinkErrorCode.ProtocolViolation)
                        SharpLinkTelemetry.RecordProtocolFailure("server");
                    else if (authResult.ErrorCode is SharpLinkErrorCode.AuthenticationRejected or
                             SharpLinkErrorCode.AuthenticationExpired or
                             SharpLinkErrorCode.AuthorizationDenied or
                             SharpLinkErrorCode.PermissionDenied)
                        SharpLinkTelemetry.RecordAuthenticationFailure("server");
                    await session.SendHandshakeErrorAndFlushAsync(
                        authResult.ErrorCode,
                        authResult.ErrorMessage,
                        _protocolOptions.MaxErrorMessageBytes,
                        ct).ConfigureAwait(false);
                }
            
                handshakeResult = authResult;
                break;
            }
            // The first request can be coalesced with the handshake request. Preserve the
            // unconsumed remainder as unexamined when handing the reader to the request loop.
            reader.AdvanceTo(buffer.Start, handshakeResult.HasValue ? buffer.Start : buffer.End);
            
            if (handshakeResult.HasValue)
                return handshakeResult.Value;
            
            if(result.IsCompleted)
                break;
        }

        return SharpLinkAuthenticationResult.Reject(
            SharpLinkErrorCode.ConnectionClosed,
            "Client disconnected during handshake.");
    }

    private ISharpLinkCompressionProvider? SelectCompressionProvider(
        in ProtocolV2HandshakeRequest request)
    {
        if ((request.SupportedCapabilities & ProtocolV2Capabilities.Compression) == 0 ||
            request.CompressionProfiles.IsEmpty)
        {
            return null;
        }

        foreach (var provider in _runtimeContext.Compression.Providers)
        {
            foreach (var profile in request.CompressionProfiles.Span)
            {
                if (string.Equals(provider.WireProfile, profile, StringComparison.Ordinal))
                    return provider;
            }
        }
        return null;
    }

    private async ValueTask<SharpLinkAuthenticationResult> AuthenticateAsync(
        IRpcSession session,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (_authenticator is null)
        {
            return _authenticationRequired
                ? SharpLinkAuthenticationResult.Reject()
                : SharpLinkAuthenticationResult.Success;
        }

        try
        {
            var rpcSession = (RpcSession)session;
            var result = await _authenticator.AuthenticateAsync(
                new SharpLinkAuthenticationRequest(
                    session.Id,
                    payload,
                    rpcSession.LocalEndPoint,
                    rpcSession.RemoteEndPoint),
                cancellationToken).ConfigureAwait(false);
            if (result.IsAuthenticated && result.Context?.IsExpired() == true)
            {
                return SharpLinkAuthenticationResult.Reject(
                    SharpLinkErrorCode.AuthenticationExpired,
                    "Authentication token has expired.");
            }
            if (!result.IsAuthenticated && result.ErrorCode == SharpLinkErrorCode.Unknown)
            {
                return SharpLinkAuthenticationResult.Reject(
                    SharpLinkErrorCode.AuthenticationRejected,
                    result.ErrorMessage);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogAuthenticationProviderFailed(_logger, exception);
            return SharpLinkAuthenticationResult.Reject(
                SharpLinkErrorCode.AuthenticationRejected,
                "Authentication failed.");
        }
    }
    private async Task ProcessRequestLoop(ServerConnectionState connection)
    {
        var session = connection.Session;
        var ct = connection.ConnectionToken;
        var reader = session.Input;
        var requestCancellationMap = connection.CallCancellations;
        try
        {
            //处理握手
            while (session.IsConnected && !ct.IsCancellationRequested)
            {
                // 1. 等待数据读取
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;

                try
                {
                    // 2. 循环解析 buffer 中的数据包 (可能包含多个包)
                    while (ProtocolV2FrameParser.TryReadFrame(ref buffer, _protocolOptions, out var header, out var payload))
                    {
                        SharpLinkTelemetry.RecordReceivedBytes(ProtocolV2Constants.HeaderBytes + payload.Length);
                        session.LastActive = DateTime.UtcNow;
                        IRpcByteBufferWriter? decodedOwner = null;
                        try
                        {
                            if (header.Type == ProtocolV2FrameType.StreamData &&
                                (header.Flags & ProtocolV2FrameFlags.Compressed) != 0 &&
                                session.StreamManager is StreamManager preAdmissionStreams)
                            {
                                var rpcSession = (RpcSession)session;
                                rpcSession.ValidateInboundPayloadEnvelope(
                                    header.Type, header.Flags, payload);
                                var requestId = unchecked((long)header.RequestId);
                                var streamId = RpcSession.ReadCompressedStreamId(payload);
                                var originalLength = RpcSession.ReadCompressedOriginalLength(
                                    header.Type, header.Flags, payload);
                                if (preAdmissionStreams.TryDispatchPreAdmissionCompressed(
                                        requestId,
                                        streamId,
                                        payload,
                                        originalLength,
                                        out var preAdmissionDispatch))
                                {
                                    await preAdmissionDispatch.ConfigureAwait(false);
                                    continue;
                                }
                            }
                            if (header.Type == ProtocolV2FrameType.Request &&
                                _admissionController is not null)
                            {
                                ((RpcSession)session).ValidateInboundPayloadEnvelope(
                                    header.Type, header.Flags, payload);
                            }
                            else
                            {
                                payload = ((RpcSession)session).DecodeInboundPayload(
                                    header.Type, header.Flags, payload, ct, out decodedOwner);
                            }
                        }
                        catch (SharpLinkException exception) when (
                            exception.Code is SharpLinkErrorCode.DataLoss or SharpLinkErrorCode.Internal)
                        {
                            var failedRequestId = unchecked((long)header.RequestId);
                            if (header.Type == ProtocolV2FrameType.Request)
                            {
                                if ((header.Flags & ProtocolV2FrameFlags.OneWay) != 0)
                                {
                                    Interlocked.Increment(ref _rejectedOneWayCalls);
                                    DrainRejectedOneWayStreams(
                                        session,
                                        failedRequestId,
                                        ResolveRawRequestClientStreamCount(payload));
                                }
                                else
                                    session.SendRpcErrorAsync(failedRequestId, exception);
                            }
                            else if (header.Type == ProtocolV2FrameType.StreamData)
                            {
                                session.StreamManager.CompleteStream(
                                    failedRequestId,
                                    RpcSession.ReadCompressedStreamId(payload),
                                    exception);
                            }
                            continue;
                        }
                        // 3. 处理完整的消息 (这里不需要 await 阻塞网络读取，最好由 Task.Run 处理业务)
                        // 注意：messagePayload 在 Advance 之后就会失效，如果需要异步处理，必须 Copy
                        try
                        {
                            switch (header.Type)
                            {
                            case ProtocolV2FrameType.Ping:
                                DebugLogClientHeartbeatReceived(_logger);
                                session.LastActive = DateTime.UtcNow;
                                session.SendPongAsync(ReadMonotonicTimestamp(payload));
                                break;
                            case ProtocolV2FrameType.Pong:
                                DebugLogClientHeartbeatReceived(_logger);
                                break;
                            case ProtocolV2FrameType.Request:
                            {
                                var requestId = unchecked((long)header.RequestId);
                                using var requestScope = BeginRequestLogScope(_logger, requestId);
                                if (!TryAcceptRequest(connection, requestId))
                                {
                                    if ((header.Flags & ProtocolV2FrameFlags.OneWay) != 0)
                                    {
                                        Interlocked.Increment(ref _rejectedOneWayCalls);
                                        LogOnewayRpcResourceExhausted(_logger);
                                    }
                                    else
                                    {
                                        session.SendRpcErrorAsync(requestId, new SharpLinkException(
                                            SharpLinkErrorCode.Unavailable,
                                            "Server is draining."));
                                    }
                                    break;
                                }

                                if ((header.Flags & ProtocolV2FrameFlags.OneWay) != 0)
                                {
                                    DispatchOneWayRpc(
                                        connection, requestId, header.Flags, payload, requestCancellationMap, ct);
                                    break;
                                }

                                var dispatchTask = DispatchRpcAsync(
                                    connection, requestId, header.Flags, payload, requestCancellationMap, ct);
                                if (!dispatchTask.IsCompletedSuccessfully)
                                    ObserveUserCall(dispatchTask, requestId);
                                break;
                            }
                            case ProtocolV2FrameType.Cancel:
                                var cancelRequestId = unchecked((long)header.RequestId);
                                var cancelReason = session.ReadNegotiatedCancelReason(payload);
                                ((RpcSession)session).AbortSendStreams(
                                    cancelRequestId,
                                    CreateRemoteCancellationException(cancelReason));
                                if (requestCancellationMap.TryGetValue(cancelRequestId, out var callState) &&
                                    callState.TryAcquire(cancelRequestId))
                                {
                                    try
                                    {
                                        callState.TryCancel(MapRemoteCancellationReason(cancelReason));
                                    }
                                    finally
                                    {
                                        callState.ReleaseUse();
                                    }
                                }
                                break;
                            case ProtocolV2FrameType.StreamData:
                                await DispatchStreamChunkAsync(session, unchecked((long)header.RequestId), payload);
                                break;
                            case ProtocolV2FrameType.StreamComplete:
                                DispatchStreamComplete(
                                    session, unchecked((long)header.RequestId), header.Flags, payload, _protocolOptions);
                                break;
                            case ProtocolV2FrameType.WindowUpdate:
                                ((RpcSession)session).ApplyWindowUpdate(
                                    unchecked((long)header.RequestId),
                                    ProtocolV2PayloadCodec.ReadWindowUpdate(payload));
                                break;
                            case ProtocolV2FrameType.GoAway:
                                await session.DisposeAsync();
                                return;
                            case ProtocolV2FrameType.HealthCheck:
                                if ((((RpcSession)session).NegotiatedCapabilities &
                                     ProtocolV2Capabilities.HealthCheck) == 0)
                                {
                                    throw new SharpLinkException(
                                        SharpLinkErrorCode.ProtocolViolation,
                                        "HealthCheck was not negotiated for this session.");
                                }
                                session.SendHealthResponse(
                                    unchecked((long)header.RequestId),
                                    HealthStatus);
                                break;
                            case ProtocolV2FrameType.HandshakeRequest:
                            case ProtocolV2FrameType.HandshakeResponse:
                            case ProtocolV2FrameType.Response:
                            case ProtocolV2FrameType.HealthResponse:
                                default:
                                {
                                    SharpLinkTelemetry.RecordProtocolFailure("server");
                                    await session.DisposeAsync();
                                    break;
                                }
                            }
                        }
                        finally
                        {
                            ((RpcSession)session).ReturnDecodedPayload(decodedOwner);
                        }
                    }

                    // 4. 告诉 Pipe 我们消费到了哪里
                    if (result.IsCompleted) break;
                }
                finally
                {
                    // 移动游标：buffer.Start 是我们没处理完的起始位置
                    reader.AdvanceTo(buffer.Start, buffer.End);
                }
            }
        }
        finally
        {
            // Outstanding observers retain the map and remove their own state after user work ends.
            // Cooperative methods already observe serverLoopToken; non-cooperative methods suppress
            // their response because the token is checked before response ownership is claimed.
        }
    }

    private async Task AwaitDispatchAsync(ValueTask dispatchTask, long requestId)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        try
        {
            await dispatchTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogRpcDispatchUnhandledException(_logger, ex);
        }
    }

    private void ObserveUserCall(ValueTask dispatchTask, long requestId)
        => _ = AwaitDispatchAsync(dispatchTask, requestId);

    private void DispatchOneWayRpc(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState? admittedCallState = null,
        bool admissionGranted = false,
        int admittedClientStreamCount = 0)
    {
        var session = connection.Session;
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        var isCancellable = (flags & ProtocolV2FrameFlags.Cancellable) != 0;
        var request = ReadRequestEnvelope(session, payload, flags);
        if (IsDeadlineExceeded(request.DeadlineTimestamp))
        {
            if (admittedCallState is not null)
            {
                DrainRejectedOneWayStreams(session, requestId, admittedClientStreamCount);
                ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
            }
            return;
        }
        if (!Volatile.Read(ref _services).TryGetValue(request.InterfaceHash, out var serviceInfo))
        {
            if (admittedCallState is not null)
            {
                DrainRejectedOneWayStreams(session, requestId, admittedClientStreamCount);
                ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
            }
            return;
        }
        if (!serviceInfo.AcceptsCalls)
        {
            if (admittedCallState is not null)
            {
                DrainRejectedOneWayStreams(session, requestId, admittedClientStreamCount);
                ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
            }
            return;
        }

        var descriptor = GetMethodDescriptor(serviceInfo.Stub, request.MethodHash);

        if (_admissionController is not null && !admissionGranted)
        {
            admittedCallState = CreateAdmissionWaitState(
                connection,
                requestId,
                request.Deadline,
                request.DeadlineTimestamp,
                serverLoopToken,
                serviceInfo.ModuleCancellation,
                requestCancellationMap);
            ValueTask<AdmissionDecision> admissionTask;
            try
            {
                admissionTask = _admissionController.AcquireAsync(
                    CreateAdmissionContext(connection, descriptor, request),
                    checked((int)payload.Length),
                    _admissionController.QueueOneWayCalls,
                    admittedCallState.InvocationToken);
            }
            catch (Exception exception)
            {
                LogOnewayRpcDispatchFailed(_logger, exception);
                DrainRejectedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                RejectAdmission(
                    session,
                    requestId,
                    AdmissionDecision.Reject(
                        "partition_selector", "partition", SharpLinkErrorCode.Internal),
                    oneWay: true);
                ReleaseAdmissionCallState(
                    requestCancellationMap, requestId, admittedCallState);
                return;
            }
            if (!admissionTask.IsCompletedSuccessfully)
            {
                ReservePreAdmissionRequestStreams(
                    session,
                    requestId,
                    descriptor.ClientStreamCount,
                    admittedCallState);
                var retainedPayload = CopyAdmissionPayload(payload);
                ObserveUserCall(
                    new ValueTask(AwaitOneWayAdmissionAsync(
                        admissionTask,
                        retainedPayload,
                        connection,
                        requestId,
                        flags,
                        requestCancellationMap,
                        serverLoopToken,
                        descriptor.ClientStreamCount,
                        admittedCallState)),
                    requestId);
                return;
            }

            var decision = admissionTask.Result;
            if (!decision.IsAcquired)
            {
                DrainRejectedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
                RejectAdmission(connection.Session, requestId, decision, oneWay: true);
                ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
                return;
            }
            admittedCallState.AttachAdmissionLease(decision.Lease!);
        }

        var admission = TryAcquireCall(connection);
        if (admission != ServerCallAdmissionResult.Acquired)
        {
            DrainRejectedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
            if (admittedCallState is not null)
                ReleaseAdmissionCallState(requestCancellationMap, requestId, admittedCallState);
            Interlocked.Increment(ref _rejectedOneWayCalls);
            if (admission == ServerCallAdmissionResult.CapacityExhausted)
            {
                SharpLinkTelemetry.RecordResourceExhausted("server");
                LogOnewayRpcResourceExhausted(_logger);
            }
            return;
        }

        IRpcByteBufferWriter? decodedRequestOwner = null;
        try
        {
            if (_admissionController is not null)
            {
                payload = ((RpcSession)session).DecodeInboundPayload(
                    ProtocolV2FrameType.Request,
                    flags,
                    payload,
                    admittedCallState?.InvocationToken ?? serverLoopToken,
                    out decodedRequestOwner);
                request = ReadRequestEnvelope(session, payload, flags);
            }
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.DataLoss or SharpLinkErrorCode.Internal)
        {
            Interlocked.Increment(ref _rejectedOneWayCalls);
            LogOnewayRpcDispatchFailed(_logger, exception);
            DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
            ReleaseOneWayDispatchResources(
                admittedCallState, requestId, requestCancellationMap, connection);
            return;
        }
        catch (OperationCanceledException)
        {
            DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
            ReleaseOneWayDispatchResources(
                admittedCallState, requestId, requestCancellationMap, connection);
            return;
        }
        catch
        {
            ((RpcSession)session).ReturnDecodedPayload(decodedRequestOwner);
            DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
            ReleaseOneWayDispatchResources(
                admittedCallState, requestId, requestCancellationMap, connection);
            throw;
        }

        var supportsCooperativeCancellation =
            (isCancellable || serviceInfo.Module is not null) &&
            serviceInfo.Stub.SupportsCancellation(request.MethodHash);
        var callState = admittedCallState ?? CreateTrackedCallState(
            connection,
            requestId,
            request.Deadline,
            request.DeadlineTimestamp,
            serverLoopToken,
            serviceInfo.ModuleCancellation,
            supportsCooperativeCancellation,
            requestCancellationMap);
        if (decodedRequestOwner is not null)
        {
            callState = EnsureTrackedCallState(
                connection, callState, requestId, request.Deadline, request.DeadlineTimestamp,
                serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
            callState.AttachPayloadOwner(_runtimeContext.Buffers, decodedRequestOwner);
            decodedRequestOwner = null;
        }
        var invokeToken = supportsCooperativeCancellation
            ? callState!.InvocationToken
            : serverLoopToken;

        var callContext = CreateCallContext(
            connection, serviceInfo.Stub, request.MethodHash, requestId,
            request.Deadline, request.Metadata, invokeToken);
        try
        {
            using var callContextScope = SharpLinkCallContext.Push(callContext);
            var invokeTask = InvokeServiceAsync(
                serviceInfo,
                connection,
                session,
                request.MethodHash,
                requestId,
                request.Arguments,
                output: null,
                invokeToken,
                callContext);
            if (invokeTask.IsCompletedSuccessfully)
            {
                if (callContext is SharpLinkServerInvocationContext interceptorContext)
                    interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
                TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken);
                ReleaseOneWayDispatchResources(callState, requestId, requestCancellationMap, connection);
                return;
            }

            callState = EnsureTrackedCallState(
                connection, callState, requestId, request.Deadline, request.DeadlineTimestamp,
                serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
            ObserveUserCall(
                new ValueTask(AwaitOneWayDispatchAsync(
                    invokeTask,
                    callState,
                    requestId,
                    requestCancellationMap,
                    connection,
                    callContext,
                    session,
                    serviceInfo.Stub,
                    request.MethodHash,
                    invokeToken)),
                requestId);
        }
        catch (Exception ex)
        {
            DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
            if (TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
            {
                LogOnewayRpcDispatchFailed(_logger, MapServiceException(
                    ex, callContext, session, serviceInfo.Stub, request.MethodHash, requestId, invokeToken));
            }
            ReleaseOneWayDispatchResources(callState, requestId, requestCancellationMap, connection);
        }
    }

    private async Task AwaitOneWayDispatchAsync(
        ValueTask invokeTask,
        ServerCallCancellationState callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        SharpLinkCallContextSnapshot callContext,
        IRpcSession session,
        IRpcStub stub,
        long methodId,
        CancellationToken cancellationToken)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        try
        {
            await invokeTask.ConfigureAwait(false);
            if (callContext is SharpLinkServerInvocationContext interceptorContext)
                interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
            TryClaimCallCompletion(callState);
        }
        catch (Exception ex)
        {
            if (TryClaimCallCompletion(callState))
            {
                LogOnewayRpcDispatchFailed(_logger, MapServiceException(
                    ex, callContext, session, stub, methodId, requestId, cancellationToken));
            }
        }
        finally
        {
            ReleaseOneWayDispatchResources(callState, requestId, requestCancellationMap, connection);
        }
    }

    private async Task AwaitOneWayAdmissionAsync(
        ValueTask<AdmissionDecision> admissionTask,
        IRpcByteBufferWriter retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        int clientStreamCount,
        ServerCallCancellationState callState)
    {
        var transferred = false;
        try
        {
            AdmissionDecision decision;
            try
            {
                decision = await admissionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                decision = CreateAdmissionCancellationDecision(callState);
            }
            if (!decision.IsAcquired)
            {
                DrainRejectedOneWayStreams(
                    connection.Session, requestId, clientStreamCount);
                RejectAdmission(connection.Session, requestId, decision, oneWay: true);
                ReleaseAdmissionCallState(requestCancellationMap, requestId, callState);
                transferred = true;
                return;
            }

            callState.AttachAdmissionLease(decision.Lease!);
            DispatchOneWayRpc(
                connection,
                requestId,
                flags,
                new ReadOnlySequence<byte>(retainedPayload.WrittenMemory),
                requestCancellationMap,
                serverLoopToken,
                callState,
                admissionGranted: true,
                admittedClientStreamCount: clientStreamCount);
            transferred = true;
        }
        finally
        {
            _runtimeContext.Buffers.Return(retainedPayload);
            if (!transferred)
                ReleasePendingAdmissionState(connection.Session, requestCancellationMap, requestId, callState);
        }
    }

    private async ValueTask AwaitRpcAdmissionAsync(
        ValueTask<AdmissionDecision> admissionTask,
        IRpcByteBufferWriter retainedPayload,
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState callState)
    {
        var transferred = false;
        try
        {
            AdmissionDecision decision;
            try
            {
                decision = await admissionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                decision = CreateAdmissionCancellationDecision(callState);
            }
            if (!decision.IsAcquired)
            {
                RejectAdmission(connection.Session, requestId, decision, oneWay: false);
                return;
            }

            callState.AttachAdmissionLease(decision.Lease!);
            var dispatchTask = DispatchRpcAsync(
                connection,
                requestId,
                flags,
                new ReadOnlySequence<byte>(retainedPayload.WrittenMemory),
                requestCancellationMap,
                serverLoopToken,
                callState,
                admissionGranted: true);
            transferred = true;
            if (!dispatchTask.IsCompletedSuccessfully)
                await dispatchTask.ConfigureAwait(false);
        }
        finally
        {
            _runtimeContext.Buffers.Return(retainedPayload);
            if (!transferred)
                ReleasePendingAdmissionState(connection.Session, requestCancellationMap, requestId, callState);
        }
    }

    private ServerCallCancellationState CreateAdmissionWaitState(
        ServerConnectionState connection,
        long requestId,
        DateTimeOffset? deadline,
        long deadlineTimestamp,
        CancellationToken serverLoopToken,
        CancellationToken moduleDrainingToken,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap)
    {
        var callState = ServerCallCancellationState.Rent(
            requestId,
            deadline,
            deadlineTimestamp,
            serverLoopToken,
            _forceStopCts.Token,
            moduleDrainingToken,
            supportsCooperativeCancellation: true);
        requestCancellationMap.Set(requestId, callState);
        connection.DeadlineScheduler.Register(callState);
        return callState;
    }

    private static SharpLinkAdmissionContext CreateAdmissionContext(
        ServerConnectionState connection,
        RpcMethodDescriptor descriptor,
        RpcRequestEnvelope request)
        => new(
            descriptor.ContractId,
            descriptor.MethodId,
            descriptor.Kind,
            connection.Session.Id,
            connection.AuthenticationContext,
            request.Metadata,
            request.Deadline);

    private IRpcByteBufferWriter CopyAdmissionPayload(ReadOnlySequence<byte> payload)
    {
        var owner = _runtimeContext.Buffers.Rent(checked((int)payload.Length));
        foreach (var segment in payload)
            owner.Write(segment.Span);
        return owner;
    }

    private void RejectAdmission(
        IRpcSession session,
        long requestId,
        AdmissionDecision decision,
        bool oneWay)
    {
        var scope = decision.Scope ?? "server";
        var reason = decision.Reason ?? "unknown";
        SharpLinkTelemetry.RecordAdmissionRejected(scope, reason);
        if (decision.ErrorCode == SharpLinkErrorCode.ResourceExhausted)
            SharpLinkTelemetry.RecordResourceExhausted("server");
        if (oneWay)
        {
            Interlocked.Increment(ref _rejectedOneWayCalls);
            SharpLinkTelemetry.RecordAdmissionOneWayDropped(scope, reason);
            if (ShouldLogOneWayAdmissionRejection())
                LogOnewayRpcResourceExhausted(_logger);
            return;
        }

        session.SendRpcErrorAsync(requestId, new SharpLinkException(
            decision.ErrorCode,
            decision.ErrorCode == SharpLinkErrorCode.ResourceExhausted
                ? $"Server admission rejected the call ({scope}/{reason})."
                : "Server stopped accepting new calls."));
    }

    private bool ShouldLogOneWayAdmissionRejection()
    {
        var now = Stopwatch.GetTimestamp();
        var minimumInterval = Stopwatch.Frequency * 5L;
        while (true)
        {
            var previous = Volatile.Read(ref _oneWayAdmissionLogTimestamp);
            if (previous != 0 && now - previous < minimumInterval)
                return false;
            if (Interlocked.CompareExchange(ref _oneWayAdmissionLogTimestamp, now, previous) == previous)
                return true;
        }
    }

    private static AdmissionDecision CreateAdmissionCancellationDecision(
        ServerCallCancellationState callState)
        => callState.Reason switch
        {
            ServerCallCancellationReason.DeadlineExceeded => AdmissionDecision.Reject(
                "deadline", SharpLinkErrorCode.DeadlineExceeded),
            ServerCallCancellationReason.ConnectionClosed => AdmissionDecision.Reject(
                "disconnect", SharpLinkErrorCode.ConnectionClosed),
            ServerCallCancellationReason.AdmissionResourceExhausted => AdmissionDecision.Reject(
                "queue_bytes", SharpLinkErrorCode.ResourceExhausted),
            ServerCallCancellationReason.ServerStopping or ServerCallCancellationReason.ModuleDraining =>
                AdmissionDecision.Reject("draining", SharpLinkErrorCode.Unavailable),
            _ => AdmissionDecision.Reject("cancelled", SharpLinkErrorCode.Cancelled)
        };

    private static void ReleasePendingAdmissionState(
        IRpcSession session,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        long requestId,
        ServerCallCancellationState callState)
    {
        if (session.StreamManager is StreamManager streamManager)
        {
            streamManager.CompleteRequestStreams(
                requestId,
                new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    "Call ended before stream admission completed."));
        }
        ReleaseAdmissionCallState(requestCancellationMap, requestId, callState);
    }

    private static void ReleaseAdmissionCallState(
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        long requestId,
        ServerCallCancellationState callState)
    {
        requestCancellationMap.TryRemove(requestId, callState);
        callState.Dispose();
    }

    private void ReleaseOneWayDispatchResources(
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection)
    {
        if (callState is not null)
        {
            requestCancellationMap.TryRemove(requestId, callState);
            callState.Dispose();
        }
        ReleaseCall(connection);
    }

    private ValueTask DispatchRpcAsync(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState? admittedCallState = null,
        bool admissionGranted = false)
    {
        var session = connection.Session;
        var isCancellable = (flags & ProtocolV2FrameFlags.Cancellable) != 0;
        var hasReturnPayload = (flags & ProtocolV2FrameFlags.HasReturn) != 0;
        
        var request = ReadRequestEnvelope(session, payload, flags);
        if (IsDeadlineExceeded(request.DeadlineTimestamp))
        {
            session.SendRpcErrorAsync(requestId, new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded before dispatch."));
            if (admittedCallState is not null)
                ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
            return ValueTask.CompletedTask;
        }
        if (!Volatile.Read(ref _services).TryGetValue(request.InterfaceHash, out var serviceInfo))
        {
            session.SendRpcErrorAsync(requestId, new SharpLinkException(
                SharpLinkErrorCode.Unimplemented,
                $"Service {request.InterfaceHash} is not implemented."));
            if (admittedCallState is not null)
                ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
            return ValueTask.CompletedTask;
        }
        if (!serviceInfo.AcceptsCalls)
        {
            session.SendRpcErrorAsync(requestId, new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "RPC module is draining"));
            if (admittedCallState is not null)
                ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
            return ValueTask.CompletedTask;
        }

        if (_admissionController is not null && !admissionGranted)
        {
            admittedCallState = CreateAdmissionWaitState(
                connection,
                requestId,
                request.Deadline,
                request.DeadlineTimestamp,
                serverLoopToken,
                serviceInfo.ModuleCancellation,
                requestCancellationMap);
            var descriptor = GetMethodDescriptor(serviceInfo.Stub, request.MethodHash);
            ValueTask<AdmissionDecision> admissionTask;
            try
            {
                admissionTask = _admissionController.AcquireAsync(
                    CreateAdmissionContext(connection, descriptor, request),
                    checked((int)payload.Length),
                    allowQueue: true,
                    admittedCallState.InvocationToken);
            }
            catch (Exception exception)
            {
                session.SendRpcErrorAsync(requestId, new SharpLinkException(
                    SharpLinkErrorCode.Internal,
                    "The admission partition selector failed.",
                    exception));
                SharpLinkTelemetry.RecordAdmissionRejected("partition", "partition_selector");
                ReleasePendingAdmissionState(
                    session, requestCancellationMap, requestId, admittedCallState);
                return ValueTask.CompletedTask;
            }
            if (!admissionTask.IsCompletedSuccessfully)
            {
                ReservePreAdmissionRequestStreams(
                    session,
                    requestId,
                    descriptor.ClientStreamCount,
                    admittedCallState);
                var retainedPayload = CopyAdmissionPayload(payload);
                return AwaitRpcAdmissionAsync(
                    admissionTask,
                    retainedPayload,
                    connection,
                    requestId,
                    flags,
                    requestCancellationMap,
                    serverLoopToken,
                    admittedCallState);
            }

            var decision = admissionTask.Result;
            if (!decision.IsAcquired)
            {
                RejectAdmission(connection.Session, requestId, decision, oneWay: false);
                ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
                return ValueTask.CompletedTask;
            }
            admittedCallState.AttachAdmissionLease(decision.Lease!);
        }

        var admission = TryAcquireCall(connection);
        if (admission != ServerCallAdmissionResult.Acquired)
        {
            if (admittedCallState is not null)
                ReleasePendingAdmissionState(session, requestCancellationMap, requestId, admittedCallState);
            if (admission == ServerCallAdmissionResult.CapacityExhausted)
            {
                SharpLinkTelemetry.RecordResourceExhausted("server");
                session.SendRpcErrorAsync(requestId, new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    "Server call capacity is exhausted."));
            }
            else
            {
                session.SendRpcErrorAsync(requestId, new SharpLinkException(
                    SharpLinkErrorCode.Unavailable,
                    "Server is draining."));
            }
            return ValueTask.CompletedTask;
        }

        IRpcByteBufferWriter? decodedRequestOwner = null;
        try
        {
            if (_admissionController is not null)
            {
                payload = ((RpcSession)session).DecodeInboundPayload(
                    ProtocolV2FrameType.Request,
                    flags,
                    payload,
                    admittedCallState?.InvocationToken ?? serverLoopToken,
                    out decodedRequestOwner);
                request = ReadRequestEnvelope(session, payload, flags);
            }
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.DataLoss or SharpLinkErrorCode.Internal)
        {
            session.SendRpcErrorAsync(requestId, exception);
            CompleteFailedRequestStreams(session, requestId, exception);
            ReleaseDispatchResources(
                admittedCallState, requestId, requestCancellationMap, connection);
            return ValueTask.CompletedTask;
        }
        catch (OperationCanceledException exception)
        {
            CompleteFailedRequestStreams(session, requestId, exception);
            session.SendRpcErrorAsync(
                requestId,
                CreateServerCancellationException(admittedCallState, request.DeadlineTimestamp));
            ReleaseDispatchResources(
                admittedCallState, requestId, requestCancellationMap, connection);
            return ValueTask.CompletedTask;
        }
        catch (Exception exception)
        {
            ((RpcSession)session).ReturnDecodedPayload(decodedRequestOwner);
            CompleteFailedRequestStreams(session, requestId, exception);
            ReleaseDispatchResources(
                admittedCallState, requestId, requestCancellationMap, connection);
            throw;
        }

        var supportsCooperativeCancellation =
            (isCancellable || serviceInfo.Module is not null) &&
            serviceInfo.Stub.SupportsCancellation(request.MethodHash);
        var callState = admittedCallState ?? CreateTrackedCallState(
            connection,
            requestId,
            request.Deadline,
            request.DeadlineTimestamp,
            serverLoopToken,
            serviceInfo.ModuleCancellation,
            supportsCooperativeCancellation,
            requestCancellationMap);
        if (decodedRequestOwner is not null)
        {
            callState = EnsureTrackedCallState(
                connection, callState, requestId, request.Deadline, request.DeadlineTimestamp,
                serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
            callState.AttachPayloadOwner(_runtimeContext.Buffers, decodedRequestOwner);
            decodedRequestOwner = null;
        }
        var invokeToken = supportsCooperativeCancellation
            ? callState!.InvocationToken
            : serverLoopToken;

        if (!hasReturnPayload)
        {
            var callContext = CreateCallContext(
                connection, serviceInfo.Stub, request.MethodHash, requestId,
                request.Deadline, request.Metadata, invokeToken);
            try
            {
                using var callContextScope = SharpLinkCallContext.Push(callContext);
                var invokeTask = InvokeServiceAsync(
                    serviceInfo, connection, session, request.MethodHash, requestId,
                    request.Arguments, output: null, invokeToken, callContext);
                if (!invokeTask.IsCompletedSuccessfully)
                {
                    callState = EnsureTrackedCallState(
                        connection, callState, requestId, request.Deadline, request.DeadlineTimestamp,
                        serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
                    return AwaitDispatchRpcNoReturnAsync(
                        invokeTask, session, requestId, callState, requestCancellationMap, connection,
                        callContext, serviceInfo.Stub, request.MethodHash, invokeToken);
                }
                if (callContext is SharpLinkServerInvocationContext interceptorContext)
                    interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
                if (TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
                    session.SendPacketAsync(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, requestId);
                else
                    TrySendModuleDrainError(callState, session, requestId);
                ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
                return ValueTask.CompletedTask;
            }
            catch (OperationCanceledException exception)
            {
                CompleteFailedRequestStreams(session, requestId, exception);
                if (TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
                    session.SendRpcErrorAsync(
                        requestId,
                        CreateServerCancellationException(callState, request.DeadlineTimestamp));
                else
                    TrySendModuleDrainError(callState, session, requestId);
                ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
                return ValueTask.CompletedTask;
            }
            catch (Exception e)
            {
                CompleteFailedRequestStreams(session, requestId, e);
                if (TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
                {
                    session.SendRpcErrorAsync(requestId, MapServiceException(
                        e, callContext, session, serviceInfo.Stub, request.MethodHash, requestId, invokeToken));
                }
                else
                {
                    TrySendModuleDrainError(callState, session, requestId);
                }
                ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
                return ValueTask.CompletedTask;
            }
        }

        var writer = ((RpcSession)session).RentFrameWriter();
        var ownsWriter = true;
        var token = writer.BeginPacket(
            ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, unchecked((ulong)requestId));
        var responseCallContext = CreateCallContext(
            connection, serviceInfo.Stub, request.MethodHash, requestId,
            request.Deadline, request.Metadata, invokeToken);
        try
        {
            using var callContextScope = SharpLinkCallContext.Push(responseCallContext);
            var invokeTask = InvokeServiceAsync(
                serviceInfo, connection, session, request.MethodHash, requestId,
                request.Arguments, writer, invokeToken, responseCallContext);
            if (!invokeTask.IsCompletedSuccessfully)
            {
                callState = EnsureTrackedCallState(
                    connection, callState, requestId, request.Deadline, request.DeadlineTimestamp,
                    serverLoopToken, serviceInfo.ModuleCancellation, requestCancellationMap);
                return AwaitDispatchRpcAsync(invokeTask, session, requestId, writer, token, callState,
                    requestCancellationMap, connection, responseCallContext,
                    serviceInfo.Stub, request.MethodHash, invokeToken);
            }
            if (responseCallContext is SharpLinkServerInvocationContext interceptorContext)
                interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
            if (!TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
            {
                _runtimeContext.Buffers.Return(writer);
                TrySendModuleDrainError(callState, session, requestId);
                ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
                return ValueTask.CompletedTask;
            }
            writer.EndPacket(token);
            ownsWriter = false;
            ((RpcSession)session).SendPacket(writer);
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
            return ValueTask.CompletedTask;

        }
        catch (OperationCanceledException exception)
        {
            CompleteFailedRequestStreams(session, requestId, exception);
            if (!ownsWriter)
            {
                ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
                throw;
            }

            _runtimeContext.Buffers.Return(writer);
            if (TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
                session.SendRpcErrorAsync(
                    requestId,
                    CreateServerCancellationException(callState, request.DeadlineTimestamp));
            else
                TrySendModuleDrainError(callState, session, requestId);
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
            return ValueTask.CompletedTask;
        }
        catch (Exception e)
        {
            CompleteFailedRequestStreams(session, requestId, e);
            if (!ownsWriter)
            {
                if (e is SharpLinkCompressionProviderException)
                {
                    try
                    {
                        session.SendRpcErrorAsync(requestId, e);
                    }
                    finally
                    {
                        ReleaseDispatchResources(
                            callState, requestId, requestCancellationMap, connection);
                    }
                    return ValueTask.CompletedTask;
                }
                ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
                throw;
            }

            _runtimeContext.Buffers.Return(writer);
            if (TryClaimCallCompletion(callState, request.DeadlineTimestamp, serverLoopToken))
            {
                session.SendRpcErrorAsync(requestId, MapServiceException(
                    e, responseCallContext, session, serviceInfo.Stub, request.MethodHash, requestId, invokeToken));
            }
            else
            {
                TrySendModuleDrainError(callState, session, requestId);
            }
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
            return ValueTask.CompletedTask;
        }
    }

    private void ReservePreAdmissionRequestStreams(
        IRpcSession session,
        long requestId,
        int clientStreamCount,
        ServerCallCancellationState callState)
    {
        if (clientStreamCount == 0 || session.StreamManager is not StreamManager streamManager)
            return;

        var admissionController = _admissionController ?? throw new InvalidOperationException(
            "Pre-admission streams require an admission controller.");
        streamManager.ReservePreAdmissionStreams(
            requestId,
            clientStreamCount,
            _runtimeContext.Buffers,
            admissionController.TryReserveAdditionalQueuedBytes,
            admissionController.ReleaseAdditionalQueuedBytes,
            () => callState.TryCancel(
                ServerCallCancellationReason.AdmissionResourceExhausted),
            compressedPayload =>
            {
                var decodedPayload = ((RpcSession)session).DecodeInboundPayload(
                    ProtocolV2FrameType.StreamData,
                    ProtocolV2FrameFlags.Compressed,
                    compressedPayload,
                    callState.InvocationToken,
                    out var decodedOwner);
                return new PreAdmissionDecodedPayload(
                    decodedPayload.Slice(sizeof(ushort)),
                    decodedOwner ?? throw new InvalidOperationException(
                        "Compressed stream decoding did not return an owner."),
                    _runtimeContext.Buffers);
            });
    }

    private static void DrainRejectedOneWayStreams(
        IRpcSession session,
        long requestId,
        int clientStreamCount)
    {
        if (clientStreamCount != 0 && session.StreamManager is StreamManager streamManager)
            streamManager.DrainRejectedRequestStreams(requestId, clientStreamCount);
    }

    private int ResolveRawRequestClientStreamCount(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long contractId) ||
            !reader.TryReadLittleEndian(out long methodId) ||
            !Volatile.Read(ref _services).TryGetValue(contractId, out var registration) ||
            !registration.Stub.TryGetMethodDescriptor(methodId, out var descriptor))
        {
            return 0;
        }

        return descriptor.ClientStreamCount;
    }

    private static void CompleteFailedRequestStreams(
        IRpcSession session,
        long requestId,
        Exception exception)
    {
        if (session.StreamManager is StreamManager streamManager)
            streamManager.CompleteRequestStreams(requestId, exception);
    }

    private static void DrainFailedOneWayStreams(
        IRpcSession session,
        long requestId,
        int clientStreamCount)
    {
        if (clientStreamCount == 0 || session.StreamManager is not StreamManager streamManager)
            return;

        streamManager.DrainRejectedRequestStreams(requestId, clientStreamCount);
    }

    private async ValueTask AwaitDispatchRpcNoReturnAsync(
        ValueTask invokeTask,
        IRpcSession session,
        long requestId,
        ServerCallCancellationState callState,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        SharpLinkCallContextSnapshot callContext,
        IRpcStub stub,
        long methodId,
        CancellationToken cancellationToken)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        try
        {
            await invokeTask.ConfigureAwait(false);
            if (callContext is SharpLinkServerInvocationContext interceptorContext)
                interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
            if (TryClaimCallCompletion(callState))
                session.SendPacketAsync(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, requestId);
            else
                TrySendModuleDrainError(callState, session, requestId);
        }
        catch (OperationCanceledException exception)
        {
            CompleteFailedRequestStreams(session, requestId, exception);
            if (TryClaimCallCompletion(callState))
                session.SendRpcErrorAsync(
                    requestId,
                    CreateServerCancellationException(callState, callState.DeadlineTimestamp));
            else
                TrySendModuleDrainError(callState, session, requestId);
        }
        catch (Exception e)
        {
            CompleteFailedRequestStreams(session, requestId, e);
            if (TryClaimCallCompletion(callState))
            {
                session.SendRpcErrorAsync(requestId, MapServiceException(
                    e, callContext, session, stub, methodId, requestId, cancellationToken));
            }
            else
            {
                TrySendModuleDrainError(callState, session, requestId);
            }
        }
        finally
        {
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
        }
    }

    private async ValueTask AwaitDispatchRpcAsync(
        ValueTask invokeTask,
        IRpcSession session,
        long requestId,
        IRpcByteBufferWriter writer,
        PacketToken token,
        ServerCallCancellationState callState,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection,
        SharpLinkCallContextSnapshot callContext,
        IRpcStub stub,
        long methodId,
        CancellationToken cancellationToken)
    {
        var ownsWriter = true;
        try
        {
            await invokeTask.ConfigureAwait(false);
            if (callContext is SharpLinkServerInvocationContext interceptorContext)
                interceptorContext.Status = SharpLinkInvocationStatus.Succeeded;
            if (!TryClaimCallCompletion(callState))
            {
                _runtimeContext.Buffers.Return(writer);
                ownsWriter = false;
                TrySendModuleDrainError(callState, session, requestId);
                return;
            }
            writer.EndPacket(token);
            ownsWriter = false;
            ((RpcSession)session).SendPacket(writer);
        }
        catch (OperationCanceledException exception)
        {
            CompleteFailedRequestStreams(session, requestId, exception);
            if (!ownsWriter)
                throw;

            _runtimeContext.Buffers.Return(writer);
            if (TryClaimCallCompletion(callState))
                session.SendRpcErrorAsync(
                    requestId,
                    CreateServerCancellationException(callState, callState.DeadlineTimestamp));
            else
                TrySendModuleDrainError(callState, session, requestId);
        }
        catch (Exception e)
        {
            CompleteFailedRequestStreams(session, requestId, e);
            if (!ownsWriter)
            {
                if (e is SharpLinkCompressionProviderException)
                {
                    session.SendRpcErrorAsync(requestId, e);
                    return;
                }
                throw;
            }

            _runtimeContext.Buffers.Return(writer);
            if (TryClaimCallCompletion(callState))
            {
                session.SendRpcErrorAsync(requestId, MapServiceException(
                    e, callContext, session, stub, methodId, requestId, cancellationToken));
            }
            else
            {
                TrySendModuleDrainError(callState, session, requestId);
            }
        }
        finally
        {
            ReleaseDispatchResources(callState, requestId, requestCancellationMap, connection);
        }
    }

    private void ReleaseDispatchResources(
        ServerCallCancellationState? callState,
        long requestId,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        ServerConnectionState connection)
    {
        if (callState is not null)
        {
            requestCancellationMap.TryRemove(requestId, callState);
            callState.Dispose();
        }
        ReleaseCall(connection);
    }

    private static async Task DispatchStreamChunkAsync(IRpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short streamIdBits))
            throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "StreamData stream ID is truncated.");
        var streamId = unchecked((ushort)streamIdBits);
        var streamPayload = payload.Slice(sizeof(ushort));
        await session.StreamManager.DispatchChunkAsync(requestId, streamId, streamPayload);
    }

    private static void DispatchStreamComplete(
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
            return;
        }
        var error = ProtocolV2PayloadCodec.ReadError(payload, flags, limits.MaxErrorMessageBytes);
        session.StreamManager.CompleteStream(
            requestId, streamId, new SharpLinkException(error.Code, error.Message));
    }

    private static ushort TryReadStreamId(ref ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short streamIdBits))
            throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "StreamComplete stream ID is truncated.");
        var streamId = unchecked((ushort)streamIdBits);
        payload = payload.Slice(sizeof(ushort));
        return streamId;
    }

    private static long ReadMonotonicTimestamp(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long timestamp))
            throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "Heartbeat timestamp is truncated.");
        return timestamp;
    }

    private ServerCallCancellationState? CreateTrackedCallState(
        ServerConnectionState connection,
        long requestId,
        DateTimeOffset? deadline,
        long deadlineTimestamp,
        CancellationToken serverLoopToken,
        CancellationToken moduleDrainingToken,
        bool supportsCooperativeCancellation,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        AdmissionLease? admissionLease = null)
    {
        if (!supportsCooperativeCancellation && !moduleDrainingToken.CanBeCanceled && admissionLease is null)
            return null;

        var callState = ServerCallCancellationState.Rent(
            requestId,
            deadline,
            deadlineTimestamp,
            serverLoopToken,
            _forceStopCts.Token,
            moduleDrainingToken,
            supportsCooperativeCancellation);
        if (admissionLease is not null)
            callState.AttachAdmissionLease(admissionLease);
        requestCancellationMap.Set(requestId, callState);
        connection.DeadlineScheduler.Register(callState);
        return callState;
    }

    private ServerCallCancellationState EnsureTrackedCallState(
        ServerConnectionState connection,
        ServerCallCancellationState? callState,
        long requestId,
        DateTimeOffset? deadline,
        long deadlineTimestamp,
        CancellationToken serverLoopToken,
        CancellationToken moduleDrainingToken,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap)
    {
        if (callState is not null)
            return callState;

        callState = ServerCallCancellationState.Rent(
            requestId,
            deadline,
            deadlineTimestamp,
            serverLoopToken,
            _forceStopCts.Token,
            moduleDrainingToken,
            supportsCooperativeCancellation: false);
        requestCancellationMap.Set(requestId, callState);
        connection.DeadlineScheduler.Register(callState);
        return callState;
    }

    private bool TryClaimCallCompletion(ServerCallCancellationState callState)
    {
        if (callState.TryClaimResponse())
            return true;
        if (callState.TryRecordAbandoned())
        {
            SharpLinkTelemetry.RecordAbandonedCall(
                "server",
                GetTerminationReasonTag(callState.Reason));
            LogRpcCallAbandoned(_logger, callState.Reason);
        }
        return false;
    }

    private bool TryClaimCallCompletion(
        ServerCallCancellationState? callState,
        long deadlineTimestamp,
        CancellationToken serverLoopToken)
    {
        if (callState is not null)
            return TryClaimCallCompletion(callState);

        var reason = IsDeadlineExceeded(deadlineTimestamp)
            ? ServerCallCancellationReason.DeadlineExceeded
            : serverLoopToken.IsCancellationRequested
                ? ServerCallCancellationReason.ConnectionClosed
                : ServerCallCancellationReason.None;
        if (reason == ServerCallCancellationReason.None)
            return true;

        SharpLinkTelemetry.RecordAbandonedCall("server", GetTerminationReasonTag(reason));
        LogRpcCallAbandoned(_logger, reason);
        return false;
    }

    private static SharpLinkException CreateServerCancellationException(
        ServerCallCancellationState? callState,
        long deadlineTimestamp)
        => (callState?.Reason ?? (IsDeadlineExceeded(deadlineTimestamp)
            ? ServerCallCancellationReason.DeadlineExceeded
            : ServerCallCancellationReason.RemoteCancel)) switch
        {
            ServerCallCancellationReason.DeadlineExceeded => new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded."),
            ServerCallCancellationReason.ServerStopping => new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "Server is stopping."),
            ServerCallCancellationReason.ModuleDraining => new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "RPC module is draining"),
            ServerCallCancellationReason.ConnectionClosed => new SharpLinkException(
                SharpLinkErrorCode.ConnectionClosed,
                "Connection closed."),
            ServerCallCancellationReason.AdmissionResourceExhausted => new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                "Admission queue retained-byte capacity was exhausted."),
            _ => new SharpLinkException(SharpLinkErrorCode.Cancelled, "Request canceled.")
        };

    private static void TrySendModuleDrainError(
        ServerCallCancellationState? callState,
        IRpcSession session,
        long requestId)
    {
        if (callState?.TryClaimModuleDrainResponse() == true)
        {
            session.SendRpcErrorAsync(requestId, new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "RPC module is draining"));
        }
    }

    private static ServerCallCancellationReason MapRemoteCancellationReason(
        ProtocolV2CancelReason reason)
        => reason switch
        {
            ProtocolV2CancelReason.DeadlineExceeded => ServerCallCancellationReason.DeadlineExceeded,
            ProtocolV2CancelReason.ConsumerAbandoned => ServerCallCancellationReason.ConsumerAbandoned,
            ProtocolV2CancelReason.Unspecified or
            ProtocolV2CancelReason.UserCancellation => ServerCallCancellationReason.RemoteCancel,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        };

    private static string GetTerminationReasonTag(ServerCallCancellationReason reason)
        => reason switch
        {
            ServerCallCancellationReason.RemoteCancel => "remote_cancel",
            ServerCallCancellationReason.ConsumerAbandoned => "consumer_abandoned",
            ServerCallCancellationReason.DeadlineExceeded => "deadline_exceeded",
            ServerCallCancellationReason.ModuleDraining => "module_draining",
            ServerCallCancellationReason.ServerStopping => "server_stopping",
            ServerCallCancellationReason.ConnectionClosed => "connection_closed",
            ServerCallCancellationReason.AdmissionResourceExhausted => "admission_resource_exhausted",
            _ => "unknown"
        };

    private static SharpLinkException CreateRemoteCancellationException(
        ProtocolV2CancelReason reason)
        => reason switch
        {
            ProtocolV2CancelReason.DeadlineExceeded => new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Remote RPC deadline exceeded."),
            ProtocolV2CancelReason.ConsumerAbandoned => new SharpLinkException(
                SharpLinkErrorCode.Cancelled,
                "Remote consumer abandoned the RPC stream."),
            _ => new SharpLinkException(
                SharpLinkErrorCode.Cancelled,
                "Remote caller cancelled the RPC stream.")
        };

    private RpcRequestEnvelope ReadRequestEnvelope(
        IRpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long interfaceHash) ||
            !reader.TryReadLittleEndian(out long methodHash))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ProtocolViolation,
                "Request routing prefix is truncated.");
        }

        DateTimeOffset? deadline = null;
        var deadlineTimestamp = 0L;
        if ((flags & ProtocolV2FrameFlags.HasDeadline) != 0)
        {
            if (!reader.TryReadLittleEndian(out long unixMilliseconds))
                throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "Request deadline is truncated.");
            try
            {
                deadline = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
                var utcNow = DateTimeOffset.UtcNow;
                var monotonicNow = Stopwatch.GetTimestamp();
                deadlineTimestamp = GetMonotonicDeadlineTimestamp(
                    deadline.Value,
                    utcNow,
                    monotonicNow);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Request deadline is outside the supported UTC range.",
                    exception);
            }
        }

        SharpLinkMetadata? metadata = null;
        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0)
        {
            if (session is not RpcSession runtimeSession ||
                (runtimeSession.NegotiatedCapabilities & ProtocolV2Capabilities.Metadata) == 0)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Request metadata was not negotiated during handshake.");
            }
            if (!ProtocolV2PayloadCodec.TryReadVarUInt32(ref reader, out var metadataLength) ||
                metadataLength > _protocolOptions.MaxMetadataBytes ||
                reader.Remaining < metadataLength)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Request metadata length is invalid.");
            }
            metadata = ProtocolV2PayloadCodec.ReadMetadata(
                reader.Sequence.Slice(reader.Position, metadataLength));
            reader.Advance(metadataLength);
        }

        return new RpcRequestEnvelope(
            interfaceHash,
            methodHash,
            reader.UnreadSequence,
            deadline,
            deadlineTimestamp,
            metadata);
    }

    private readonly record struct RpcRequestEnvelope(
        long InterfaceHash,
        long MethodHash,
        ReadOnlySequence<byte> Arguments,
        DateTimeOffset? Deadline,
        long DeadlineTimestamp,
        SharpLinkMetadata? Metadata);

    private static bool IsDeadlineExceeded(long deadlineTimestamp)
        => deadlineTimestamp > 0 && deadlineTimestamp <= Stopwatch.GetTimestamp();

    private static long GetMonotonicDeadlineTimestamp(
        DateTimeOffset deadline,
        DateTimeOffset utcNow,
        long monotonicNow)
    {
        var remaining = deadline - utcNow;
        if (remaining <= TimeSpan.Zero)
            return monotonicNow;
        var stopwatchTicks = remaining.TotalSeconds * Stopwatch.Frequency;
        if (stopwatchTicks >= long.MaxValue - monotonicNow)
            return long.MaxValue;
        return monotonicNow + Math.Max(1L, (long)Math.Ceiling(stopwatchTicks));
    }


}
