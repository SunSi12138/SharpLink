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
        TrackBackgroundTask(RunHeartbeatCheckLoopAsync(_forceStopCts.Token));

        try
        {
            while (!acceptToken.IsCancellationRequested)
            {
                ITransportConnection? connection = null;
                try
                {
                    connection = await transportListener.AcceptAsync(acceptToken).ConfigureAwait(false);
                    TrackBackgroundTask(HandleAcceptedConnectionAsync(connection, _forceStopCts.Token));
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
            _acceptCts.Cancel();
            _forceStopCts.Cancel();
            await transportListener.DisposeAsync().ConfigureAwait(false);
            await DisposeAllSessionsAsync().ConfigureAwait(false);
            await WaitForBackgroundTasksAsync().ConfigureAwait(false);
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
        try
        {
            if (connection is ITransportSecurityHandshake securityHandshake)
                await securityHandshake.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
            if (connection is ITransportSecurityInfo securityInfo)
                LogTlsEstablished(_logger, securityInfo.Protocol, securityInfo.CipherSuite);

            var session = new RpcSession(connection, _rpcSessionFlushOptions);
            connection = null;
            session.BindRuntimeContext(_runtimeContext);
            await ReplaceSessionAsync(session).ConfigureAwait(false);
            await HandleSessionLifecycleAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedCancellation(exception, cancellationToken))
        {
        }
        catch (Exception exception) when (exception is AuthenticationException or SharpLinkException)
        {
            LogTlsHandshakeFailed(_logger, exception);
        }
        finally
        {
            if (connection is not null)
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

    private async Task HandleSessionLifecycleAsync(IRpcSession session, CancellationToken ct)
    {
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
            
            hasConnected = true;
            _sessionAuthContexts[session.Id] = authResult.Context;
            session.NotifyConnected();
            LogClientConnected(_logger);
            await ProcessRequestLoop(session, ct);
        }
        catch (Exception ex) when (IsExpectedCancellation(ex, ct))
        {
        }
        catch (Exception ex)
        {
            LogServerBackgroundLoopUnhandledException(_logger, nameof(ProcessRequestLoop), ex);
        }
        finally
        {
            if (hasConnected)
                LogClientDisconnected(_logger);
            await DisconnectSessionAsync(session.Id);
        }
    }

    private async ValueTask ReplaceSessionAsync(IRpcSession session)
    {
        if (_sessions.TryGetValue(session.Id, out var oldSession))
            await oldSession.DisposeAsync();
        _sessions[session.Id] = session;
    }

    private async ValueTask DisconnectSessionAsync(string sessionId)
    {
        _sessionAuthContexts.TryRemove(sessionId, out _);
        _lastAcceptedRequestIds.TryRemove(sessionId, out _);
        _sessions.TryRemove(sessionId, out var rpcSession);
        if (rpcSession is not null)
        {
            await rpcSession.DisposeAsync();
        }
    }

    private async Task HeartbeatCheckLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(heartbeatCheckInterval,ct);
            var now = DateTime.UtcNow;
            foreach (var (id,session) in _sessions)
            {
                if (now - session.LastActive <= heartbeatTimeout || !session.IsConnected)
                    continue;
                
                using var sessionScope = BeginSessionLogScope(_logger, session.Id);
                LogClientHeartbeatTimeout(_logger);
                
                if(_sessions.TryRemove(id,out var oldSession))
                    await oldSession.DisposeAsync();
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
                SharpLinkAuthenticationResult authResult;
                ProtocolV2HandshakeRequest request = default;
                var supportedCapabilities =
                    ProtocolV2Capabilities.Metadata | ProtocolV2Capabilities.FlowControl;
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
                    else
                    {
                        authResult = await AuthenticateAsync(session, request.AuthenticationPayload, ct)
                            .ConfigureAwait(false);
                    }
                }

                if (authResult.IsAuthenticated)
                {
                    var response = new ProtocolV2HandshakeResponse(
                        Math.Min(request.MinorVersion, ProtocolV2Constants.MinorVersion),
                        request.SupportedCapabilities & supportedCapabilities,
                        Math.Min(request.MaxFramePayloadBytes, _protocolOptions.MaxFramePayloadBytes),
                        Math.Min(request.StreamReceiveWindowBytes, _runtimeContext.FlowControl.StreamReceiveWindowBytes),
                        Math.Min(request.ConnectionReceiveWindowBytes, _runtimeContext.FlowControl.ConnectionReceiveWindowBytes));
                    ((RpcSession)session).NegotiatedCapabilities = response.NegotiatedCapabilities;
                    if ((response.NegotiatedCapabilities & ProtocolV2Capabilities.FlowControl) != 0)
                    {
                        ((RpcSession)session).EnableStreamFlowControl(
                            response.StreamReceiveWindowBytes,
                            response.ConnectionReceiveWindowBytes);
                    }
                    await session.SendHandshakeResponseAndFlushAsync(response, ct).ConfigureAwait(false);
                }
                else
                {
                    await session.SendHandshakeErrorAndFlushAsync(
                        authResult.ErrorCode,
                        authResult.ErrorMessage,
                        _protocolOptions.MaxErrorMessageBytes,
                        ct).ConfigureAwait(false);
                }
            
                handshakeResult = authResult;
                break;
            }
            reader.AdvanceTo(buffer.Start, buffer.End);
            
            if(handshakeResult.HasValue)
                return handshakeResult.Value;
            
            if(result.IsCompleted)
                break;
        }

        return SharpLinkAuthenticationResult.Reject(
            SharpLinkErrorCode.ConnectionClosed,
            "Client disconnected during handshake.");
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
    private async Task ProcessRequestLoop(IRpcSession session,CancellationToken ct)
    {
        var reader = session.Input;
        var requestCancellationMap = new StripedLongMap<CancellationTokenSource>(_runtimeContext.Concurrency);
        var callAdmission = new SessionCallAdmission();
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
                        session.LastActive = DateTime.UtcNow;
                        // 3. 处理完整的消息 (这里不需要 await 阻塞网络读取，最好由 Task.Run 处理业务)
                        // 注意：messagePayload 在 Advance 之后就会失效，如果需要异步处理，必须 Copy
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
                                if (CurrentState != ServerState.Running)
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

                                _lastAcceptedRequestIds[session.Id] = requestId;
                                if ((header.Flags & ProtocolV2FrameFlags.OneWay) != 0)
                                {
                                    DispatchOneWayRpc(session, requestId, header.Flags, payload, requestCancellationMap, callAdmission, ct);
                                    break;
                                }

                                var dispatchTask = DispatchRpcAsync(session, requestId, header.Flags, payload, requestCancellationMap, callAdmission, ct);
                                if (!dispatchTask.IsCompletedSuccessfully)
                                    TrackBackgroundTask(AwaitDispatchAsync(dispatchTask, requestId));
                                break;
                            }
                            case ProtocolV2FrameType.Cancel:
                                if (requestCancellationMap.TryRemove(unchecked((long)header.RequestId), out var cts))
                                {
                                    await cts.CancelAsync();
                                    cts.Dispose();
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
                            case ProtocolV2FrameType.HandshakeRequest:
                            case ProtocolV2FrameType.HandshakeResponse:
                            case ProtocolV2FrameType.Response:
                            default:
                            {
                                await session.DisposeAsync();
                                break;
                            }
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
            foreach (var cts in requestCancellationMap.DrainValues())
            {
                await cts.CancelAsync();
                cts.Dispose();
            }
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

    private void DispatchOneWayRpc(
        IRpcSession session,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<CancellationTokenSource> requestCancellationMap,
        SessionCallAdmission callAdmission,
        CancellationToken serverLoopToken)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        var isCancellable = (flags & ProtocolV2FrameFlags.Cancellable) != 0;
        var request = ReadRequestEnvelope(session, payload, flags);
        if (request.Deadline is { } oneWayDeadline && oneWayDeadline <= DateTimeOffset.UtcNow)
            return;
        if (!services.TryGetValue(request.InterfaceHash, out var serviceInfo))
            return;

        if (!TryAcquireCall(callAdmission))
        {
            Interlocked.Increment(ref _rejectedOneWayCalls);
            LogOnewayRpcResourceExhausted(_logger);
            return;
        }

        CancellationTokenSource? linkedCts = null;
        var invokeToken = serverLoopToken;
        if (isCancellable && serviceInfo.stub.SupportsCancellation(request.MethodHash))
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverLoopToken);
            requestCancellationMap.Set(requestId, linkedCts);
            ApplyDeadline(linkedCts, request.Deadline);
            invokeToken = linkedCts.Token;
        }

        try
        {
            using var callContextScope = SharpLinkCallContext.Push(CreateCallContext(
                session, request.Deadline, request.Metadata));
            var invokeTask = serviceInfo.stub.InvokeNoReturnCancellableAsync(
                serviceInfo.service,
                session,
                request.MethodHash,
                requestId,
                request.Arguments,
                invokeToken);
            if (invokeTask.IsCompletedSuccessfully)
            {
                ReleaseOneWayDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
                return;
            }

            TrackBackgroundTask(AwaitOneWayDispatchAsync(
                invokeTask,
                linkedCts,
                requestId,
                requestCancellationMap,
                callAdmission));
        }
        catch (Exception ex)
        {
            LogOnewayRpcDispatchFailed(_logger, ex);
            ReleaseOneWayDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
        }
    }

    private async Task AwaitOneWayDispatchAsync(
        ValueTask invokeTask,
        CancellationTokenSource? linkedCts,
        long requestId,
        StripedLongMap<CancellationTokenSource> requestCancellationMap,
        SessionCallAdmission callAdmission)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        try
        {
            await invokeTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogOnewayRpcDispatchFailed(_logger, ex);
        }
        finally
        {
            ReleaseOneWayDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
        }
    }

    private void ReleaseOneWayDispatchResources(
        CancellationTokenSource? linkedCts,
        long requestId,
        StripedLongMap<CancellationTokenSource> requestCancellationMap,
        SessionCallAdmission callAdmission)
    {
        linkedCts?.Dispose();
        if (linkedCts is not null)
            requestCancellationMap.TryRemove(requestId, out _);
        ReleaseCall(callAdmission);
    }

    private ValueTask DispatchRpcAsync(
        IRpcSession session,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<CancellationTokenSource> requestCancellationMap,
        SessionCallAdmission callAdmission,
        CancellationToken serverLoopToken)
    {
        var isCancellable = (flags & ProtocolV2FrameFlags.Cancellable) != 0;
        var hasReturnPayload = (flags & ProtocolV2FrameFlags.HasReturn) != 0;
        
        var request = ReadRequestEnvelope(session, payload, flags);
        if (request.Deadline is { } deadline && deadline <= DateTimeOffset.UtcNow)
        {
            session.SendRpcErrorAsync(requestId, new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded before dispatch."));
            return ValueTask.CompletedTask;
        }
        if (!services.TryGetValue(request.InterfaceHash, out var serviceInfo))
        {
            session.SendRpcErrorAsync(requestId, new SharpLinkException(
                SharpLinkErrorCode.Unimplemented,
                $"Service {request.InterfaceHash} is not implemented."));
            return ValueTask.CompletedTask;
        }

        if (!TryAcquireCall(callAdmission))
        {
            session.SendRpcErrorAsync(requestId, new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                "Server call capacity is exhausted."));
            return ValueTask.CompletedTask;
        }

        CancellationTokenSource? linkedCts = null;
        var invokeToken = serverLoopToken;
        if (isCancellable && serviceInfo.stub.SupportsCancellation(request.MethodHash))
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverLoopToken);
            requestCancellationMap.Set(requestId, linkedCts);
            ApplyDeadline(linkedCts, request.Deadline);
            invokeToken = linkedCts.Token;
        }

        if (!hasReturnPayload)
        {
            try
            {
                using var callContextScope = SharpLinkCallContext.Push(CreateCallContext(
                    session, request.Deadline, request.Metadata));
                var invokeTask = serviceInfo.stub.InvokeNoReturnCancellableAsync(
                    serviceInfo.service, session, request.MethodHash, requestId, request.Arguments, invokeToken);
                if (!invokeTask.IsCompletedSuccessfully)
                    return AwaitDispatchRpcNoReturnAsync(
                        invokeTask, session, requestId, linkedCts, requestCancellationMap, callAdmission, request.Deadline);
                session.SendPacketAsync(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, requestId);
                ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
                return ValueTask.CompletedTask;
            }
            catch (OperationCanceledException)
            {
                session.SendRpcErrorAsync(requestId, CreateServerCancellationException(request.Deadline));
                ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
                return ValueTask.CompletedTask;
            }
            catch (Exception e)
            {
                session.SendRpcErrorAsync(requestId, e);
                ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
                return ValueTask.CompletedTask;
            }
        }

        var writer = _runtimeContext.Buffers.Rent();
        var ownsWriter = true;
        var token = writer.BeginPacket(
            ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, unchecked((ulong)requestId));
        try
        {
            using var callContextScope = SharpLinkCallContext.Push(CreateCallContext(
                session, request.Deadline, request.Metadata));
            var invokeTask = serviceInfo.stub.InvokeCancellableAsync(
                serviceInfo.service, session, request.MethodHash, requestId, request.Arguments, writer, invokeToken);
            if (!invokeTask.IsCompletedSuccessfully)
                return AwaitDispatchRpcAsync(invokeTask, session, requestId, writer, token, linkedCts,
                    requestCancellationMap, callAdmission, request.Deadline);
            writer.EndPacket(token);
            ownsWriter = false;
            ((RpcSession)session).SendPacket(writer);
            ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
            return ValueTask.CompletedTask;

        }
        catch (OperationCanceledException)
        {
            if (!ownsWriter)
            {
                ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
                throw;
            }

            _runtimeContext.Buffers.Return(writer);
            session.SendRpcErrorAsync(requestId, CreateServerCancellationException(request.Deadline));
            ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
            return ValueTask.CompletedTask;
        }
        catch (Exception e)
        {
            if (!ownsWriter)
            {
                ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
                throw;
            }

            _runtimeContext.Buffers.Return(writer);
            session.SendRpcErrorAsync(requestId, e);
            ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
            return ValueTask.CompletedTask;
        }
    }

    private async ValueTask AwaitDispatchRpcNoReturnAsync(
        ValueTask invokeTask,
        IRpcSession session,
        long requestId,
        CancellationTokenSource? linkedCts,
        StripedLongMap<CancellationTokenSource> requestCancellationMap,
        SessionCallAdmission callAdmission,
        DateTimeOffset? deadline)
    {
        using var requestScope = BeginRequestLogScope(_logger, requestId);
        try
        {
            await invokeTask.ConfigureAwait(false);
            session.SendPacketAsync(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, requestId);
        }
        catch (OperationCanceledException)
        {
            session.SendRpcErrorAsync(requestId, CreateServerCancellationException(deadline));
        }
        catch (Exception e)
        {
            session.SendRpcErrorAsync(requestId, e);
        }
        finally
        {
            ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
        }
    }

    private async ValueTask AwaitDispatchRpcAsync(
        ValueTask invokeTask,
        IRpcSession session,
        long requestId,
        IRpcByteBufferWriter writer,
        PacketToken token,
        CancellationTokenSource? linkedCts,
        StripedLongMap<CancellationTokenSource> requestCancellationMap,
        SessionCallAdmission callAdmission,
        DateTimeOffset? deadline)
    {
        var ownsWriter = true;
        try
        {
            await invokeTask.ConfigureAwait(false);
            writer.EndPacket(token);
            ownsWriter = false;
            ((RpcSession)session).SendPacket(writer);
        }
        catch (OperationCanceledException)
        {
            if (!ownsWriter)
                throw;

            _runtimeContext.Buffers.Return(writer);
            session.SendRpcErrorAsync(requestId, CreateServerCancellationException(deadline));
        }
        catch (Exception e)
        {
            if (!ownsWriter)
                throw;

            _runtimeContext.Buffers.Return(writer);
            session.SendRpcErrorAsync(requestId, e);
        }
        finally
        {
            ReleaseDispatchResources(linkedCts, requestId, requestCancellationMap, callAdmission);
        }
    }

    private void ReleaseDispatchResources(
        CancellationTokenSource? linkedCts,
        long requestId,
        StripedLongMap<CancellationTokenSource> requestCancellationMap,
        SessionCallAdmission callAdmission)
    {
        linkedCts?.Dispose();
        if (linkedCts is not null)
            requestCancellationMap.TryRemove(requestId, out _);
        ReleaseCall(callAdmission);
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

    private static void ApplyDeadline(CancellationTokenSource cancellation, DateTimeOffset? deadline)
    {
        if (deadline is not { } absoluteDeadline)
            return;
        var remaining = absoluteDeadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            cancellation.Cancel();
        else
            cancellation.CancelAfter(remaining);
    }

    private static SharpLinkException CreateServerCancellationException(DateTimeOffset? deadline)
        => deadline is not null
            ? new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Request deadline exceeded.")
            : new SharpLinkException(SharpLinkErrorCode.Cancelled, "Request canceled.");

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
        if ((flags & ProtocolV2FrameFlags.HasDeadline) != 0)
        {
            if (!reader.TryReadLittleEndian(out long unixMilliseconds))
                throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "Request deadline is truncated.");
            try
            {
                deadline = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
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
            metadata);
    }

    private readonly record struct RpcRequestEnvelope(
        long InterfaceHash,
        long MethodHash,
        ReadOnlySequence<byte> Arguments,
        DateTimeOffset? Deadline,
        SharpLinkMetadata? Metadata);


}
