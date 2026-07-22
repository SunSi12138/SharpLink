namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_cluster is not null)
            return _cluster.ConnectAsync(cancellationToken);

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
        var connected = new List<ClientConnection>(_connectionPoolOptions.MinConnections);
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
                RemoveReadyConnection(connected[index]);
                await connected[index].DisposeAsync().ConfigureAwait(false);
            }
            if (!_shutdownCts.IsCancellationRequested)
                TransitionTo(SharpLinkConnectionState.Faulted);
            throw;
        }
    }

    private async Task<ClientConnection> ConnectOneAsync(CancellationToken cancellationToken)
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
            var clientConnection = new ClientConnection(
                this,
                session,
                sessionCts,
                _protocolOptions.MaxPendingRequestsPerConnection,
                _runtimeContext.Codecs);
            var readySession = clientConnection.Session;
            readySession.OnDisconnected += exception => HandleDisconnected(
                clientConnection,
                exception ?? CreateConnectionClosedException("Transport closed."));

            Exception? poolException = null;
            lock (_poolGate)
            {
                if (_shutdownCts.IsCancellationRequested ||
                    State is SharpLinkConnectionState.Stopped)
                {
                    poolException = CreateConnectionClosedException("Client stopped while connecting.");
                }
                else if (CountReadyConnectionsLocked() >= _connectionPoolOptions.MaxConnections)
                {
                    poolException = new InvalidOperationException("The connection pool is already at capacity.");
                }
                else
                {
                    _connections.Add(clientConnection);
                    PublishReadySnapshotLocked();
                }
            }
            if (poolException is not null)
            {
                session = null;
                await clientConnection.DisposeAsync().ConfigureAwait(false);
                throw poolException;
            }

            readySession.NotifyConnected();
            TrackBackgroundTask(RunHeartbeatSendLoopAsync(clientConnection, sessionCts.Token));
            TrackBackgroundTask(RunProcessRequestLoopAsync(clientConnection, sessionCts.Token));
            session = null;
            return clientConnection;
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
            if (_shutdownCts.IsCancellationRequested || ReadyConnectionCount == 0)
                return;
            _readyTimestamp = Stopwatch.GetTimestamp();
            TransitionTo(SharpLinkConnectionState.Ready);
            _readySignal.TrySetResult(true);
        }
    }

    private void PublishReadySnapshotLocked()
    {
        var snapshot = new ClientConnection[_connections.Count];
        var index = 0;
        foreach (var candidate in _connections)
        {
            if (candidate.CanAcceptCalls)
                snapshot[index++] = candidate;
        }
        if (index != snapshot.Length)
            Array.Resize(ref snapshot, index);
        Volatile.Write(ref _readyConnections, snapshot);
    }

    private int CountReadyConnectionsLocked()
    {
        var count = 0;
        foreach (var candidate in _connections)
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

    private async Task RunHeartbeatSendLoopAsync(ClientConnection connection, CancellationToken ct)
    {
        var session = connection.Session;
        try
        {
            await HeartbeatSendLoop(connection, ct);
        }
        catch (Exception ex) when (IsExpectedCancellation(ex, ct))
        {
        }
        catch (Exception ex)
        {
            using var sessionScope = BeginSessionLogScope(_logger, session.Id);
            LogClientBackgroundLoopUnhandledException(_logger, nameof(HeartbeatSendLoop), ex);
            HandleDisconnected(connection, IsTransportFault(ex)
                ? CreateConnectionClosedException("Transport closed.", ex)
                : ex);
        }
    }

    private async Task RunProcessRequestLoopAsync(ClientConnection connection, CancellationToken ct)
    {
        var session = connection.Session;
        try
        {
            await ProcessRequestLoop(connection, ct);
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
            HandleDisconnected(connection, IsTransportFault(ex)
                ? CreateConnectionClosedException("Transport closed.", ex)
                : ex);
        }
    }

    public T Get<T>() where T : IService
    {
        if (Volatile.Read(ref _proxies).TryGetValue(typeof(T), out var registration))
        {
            IRpcChannel channel = registration.Module is null
                ? this
                : new SharpLinkModuleRpcChannel(this, registration.Module);
            return (T)registration.Descriptor.ProxyFactory(channel);
        }

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
        var compressionProfiles = _runtimeContext.Compression.Providers.Count == 0
            ? ReadOnlyMemory<string>.Empty
            : _runtimeContext.Compression.Providers
                .Select(static provider => provider.WireProfile)
                .ToArray();
        var supportedCapabilities =
            ProtocolV2Capabilities.Metadata |
            ProtocolV2Capabilities.FlowControl |
            ProtocolV2Capabilities.HealthCheck |
            ProtocolV2Capabilities.CancellationReason;
        if (!compressionProfiles.IsEmpty)
            supportedCapabilities |= ProtocolV2Capabilities.Compression;
        var handshakeRequest = new ProtocolV2HandshakeRequest(
            ProtocolV2Constants.MinorVersion,
            supportedCapabilities,
            ProtocolV2Capabilities.None,
            _protocolOptions.MaxFramePayloadBytes,
            _runtimeContext.FlowControl.StreamReceiveWindowBytes,
            _runtimeContext.FlowControl.ConnectionReceiveWindowBytes,
            authPayload,
            compressionProfiles);
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
                        var runtimeSession = (RpcSession)session;
                        runtimeSession.NegotiatedCapabilities = response.NegotiatedCapabilities;
                        runtimeSession.SetNegotiatedMaxFramePayloadBytes(response.MaxFramePayloadBytes);
                        var compressionProvider = ValidateNegotiatedCompression(
                            response,
                            compressionProfiles.Span);
                        if (compressionProvider is not null)
                            runtimeSession.EnableCompression(compressionProvider);
                        if ((response.NegotiatedCapabilities & ProtocolV2Capabilities.FlowControl) != 0)
                        {
                            runtimeSession.EnableStreamFlowControl(
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
            // A control or response frame can share this read with the handshake response.
            // Once the handshake is complete, leave the remainder unexamined so the request
            // loop observes it immediately instead of waiting for another transport read.
            reader.AdvanceTo(buffer.Start, handshakeCompleted ? buffer.Start : buffer.End);

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

    private ISharpLinkCompressionProvider? ValidateNegotiatedCompression(
        in ProtocolV2HandshakeResponse response,
        ReadOnlySpan<string> offeredProfiles)
    {
        var negotiated =
            (response.NegotiatedCapabilities & ProtocolV2Capabilities.Compression) != 0;
        if (!negotiated)
        {
            if (response.CompressionProfile is not null)
            {
                throw CreateProtocolViolationException(
                    "The server selected a compression profile without negotiating compression.");
            }
            return null;
        }
        if (response.CompressionProfile is not { } profile)
            throw CreateProtocolViolationException("Negotiated compression is missing its selected profile.");

        var offered = false;
        foreach (var candidate in offeredProfiles)
        {
            if (string.Equals(candidate, profile, StringComparison.Ordinal))
            {
                offered = true;
                break;
            }
        }
        var provider = offered ? _runtimeContext.Compression.FindProvider(profile) : null;
        return provider ?? throw CreateProtocolViolationException(
            $"The server selected compression profile '{profile}' that the client did not offer.");
    }

    private async Task ProcessRequestLoop(ClientConnection connection, CancellationToken ct)
    {
        var session = connection.Session;
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
                    IRpcByteBufferWriter? decodedOwner = null;
                    try
                    {
                        payload = session.DecodeInboundPayload(
                            header.Type, header.Flags, payload, ct, out decodedOwner);
                    }
                    catch (SharpLinkException exception) when (
                        exception.Code is SharpLinkErrorCode.DataLoss or SharpLinkErrorCode.Internal)
                    {
                        var requestId = unchecked((long)header.RequestId);
                        if (header.Type == ProtocolV2FrameType.Response)
                            connection.PendingCalls.DispatchError(requestId, exception);
                        else if (header.Type == ProtocolV2FrameType.StreamData)
                        {
                            var streamId = RpcSession.ReadCompressedStreamId(payload);
                            if (streamId == 0)
                            {
                                connection.PendingCalls.TryComplete(
                                    requestId,
                                    PendingCallCompletionReason.ConsumerAbandoned,
                                    exception);
                            }
                            else
                            {
                                session.StreamManager.CompleteStream(requestId, streamId, exception);
                            }
                        }
                        continue;
                    }

                    try
                    {
                        switch (header.Type)
                        {
                        case ProtocolV2FrameType.Ping:
                            session.SendPongAsync(ReadMonotonicTimestamp(payload));
                            break;
                        case ProtocolV2FrameType.Pong:
                            DebugLogServerHeartbeatReceived(_logger);
                            break;
                        case ProtocolV2FrameType.Cancel:
                            _ = session.ReadNegotiatedCancelReason(payload);
                            DebugLogServerCancelIgnored(_logger);
                            break;
                        case ProtocolV2FrameType.Response:
                            DispatchRpc(connection, unchecked((long)header.RequestId), header.Flags, ref payload);
                            break;
                        case ProtocolV2FrameType.HealthResponse:
                            DispatchHealthResponse(connection, unchecked((long)header.RequestId), ref payload);
                            break;
                        case ProtocolV2FrameType.StreamData:
                            var dispatchTask = DispatchStreamChunkAsync(session, unchecked((long)header.RequestId), payload);
                            if (!dispatchTask.IsCompletedSuccessfully)
                                await dispatchTask;
                            break;
                        case ProtocolV2FrameType.StreamComplete:
                            DispatchStreamComplete(
                                connection, unchecked((long)header.RequestId), header.Flags, payload, _protocolOptions);
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
                            MarkConnectionDraining(connection);
                            using (BeginRequestLogScope(_logger, unchecked((long)header.RequestId)))
                                LogClientDisconnectedWithError(
                                    _logger,
                                    new SharpLinkException(goAwayError.Code, goAwayError.Message));
                            break;
                        case ProtocolV2FrameType.HandshakeRequest:
                        case ProtocolV2FrameType.HandshakeResponse:
                        case ProtocolV2FrameType.Request:
                        case ProtocolV2FrameType.HealthCheck:
                            default:
                                SharpLinkTelemetry.RecordProtocolFailure("client");
                                await session.DisposeAsync();
                                HandleDisconnected(connection, CreateProtocolViolationException("Received unexpected packet from server."));
                                return;
                        }
                    }
                    finally
                    {
                        session.ReturnDecodedPayload(decodedOwner);
                    }
                }

                if (result.IsCompleted)
                    break;
            }
            finally
            {
                try
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                }
                catch (InvalidOperationException exception)
                {
                    // Transport disposal may complete the reader after ReadAsync returns but
                    // before this iteration releases its buffer. Normalize that pipe-level
                    // race to the connection error observed by every other close path.
                    throw CreateConnectionClosedException(
                        "Transport reader completed while processing a response.",
                        exception);
                }
            }
        }

        HandleDisconnected(connection, CreateConnectionClosedException("Server disconnected."));
    }

    private async Task HeartbeatSendLoop(ClientConnection connection, CancellationToken ct)
    {
        var session = connection.Session;
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
            HandleDisconnected(connection, CreateHeartbeatTimeoutException("Server heartbeat timeout."));
            break;
        }
    }

    private void DispatchRpc(
        ClientConnection connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ref ReadOnlySequence<byte> payload)
    {
        var isError = (flags & ProtocolV2FrameFlags.Error) != 0;

        if (isError)
        {
            var error = ProtocolV2PayloadCodec.ReadError(payload, flags, _protocolOptions.MaxErrorMessageBytes);
            var remoteException = new SharpLinkException(error.Code, error.Message);
            if (connection.PendingCalls.DispatchError(requestId, remoteException))
                return;
        }
        else
        {
            if (connection.PendingCalls.Dispatch(requestId, ref payload))
                return;
        }

        RecordLateResponse(connection, requestId);
    }

    private void RecordLateResponse(ClientConnection connection, long requestId)
    {
        SharpLinkTelemetry.RecordLateResponseDropped("client");
        if (!connection.ShouldLogLateResponse(out var suppressedCount))
            return;

        using var requestScope = BeginRequestLogScope(_logger, requestId);
        LogUnknownOrTimedOutResponse(_logger, suppressedCount);
    }

    private static long ReadMonotonicTimestamp(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long timestamp))
            throw CreateProtocolViolationException("Heartbeat timestamp is truncated.");
        return timestamp;
    }
}
