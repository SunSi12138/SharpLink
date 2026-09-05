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
                // The initialization attempt belongs to the client. A caller may cancel only its
                // WaitAsync below; shutdown remains the operation's lifetime boundary.
                _connectTask = ConnectInitialAsync(CancellationToken.None);
                TrackFrameworkTask(
                    _connectTask,
                    "InitialConnect",
                    TaskObservationMode.ExternallyObserved);
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
        catch (Exception connectException)
        {
            List<Exception>? cleanupExceptions = null;
            for (var index = 0; index < connected.Count; index++)
            {
                RemoveReadyConnection(connected[index]);
                try
                {
                    await connected[index].DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    (cleanupExceptions ??= []).Add(cleanupException);
                }
            }
            if (!_shutdownCts.IsCancellationRequested)
                TransitionTo(SharpLinkConnectionState.Faulted);
            if (cleanupExceptions is not null)
            {
                cleanupExceptions.Insert(0, connectException);
                throw new AggregateException(cleanupExceptions);
            }
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
            session = new RpcSession(
                connection,
                new RpcSessionCreationOptions(
                    RpcSessionRole.Client,
                    _runtimeContext,
                    _rpcSessionFlushOptions));
            connection = null;

            await CompleteHandshakeAsync(session, attemptCts.Token, cancellationToken).ConfigureAwait(false);

            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            var clientConnection = new ClientConnection(
                this,
                session,
                sessionCts,
                _protocolOptions.MaxPendingRequestsPerConnection,
                _runtimeContext);
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
                    readySession.NotifyConnected();
                    TrackFrameworkTask(
                        RunHeartbeatSendLoopAsync(clientConnection, sessionCts.Token),
                        "HeartbeatSendLoop");
                    TrackFrameworkTask(
                        RunProcessRequestLoopAsync(clientConnection, sessionCts.Token),
                        "ProcessRequestLoop");
                }
            }
            if (poolException is not null)
            {
                session = null;
                await clientConnection.DisposeAsync().ConfigureAwait(false);
                throw poolException;
            }
            session = null;
            return clientConnection;
        }
        catch (Exception exception)
        {
            await RethrowAfterFailedConnectionCleanupAsync(
                exception,
                connection,
                clientConnection: null,
                session).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }

    private static async Task RethrowAfterFailedConnectionCleanupAsync(
        Exception primaryException,
        ITransportConnection? transport,
        ClientConnection? clientConnection,
        RpcSession? session)
    {
        try
        {
            if (transport is not null)
                await transport.DisposeAsync().ConfigureAwait(false);
            if (clientConnection is not null)
                await clientConnection.DisposeAsync().ConfigureAwait(false);
            else if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupException)
        {
            throw new AggregateException(primaryException, cleanupException);
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryException).Throw();
        throw new UnreachableException();
    }

    private async Task CompleteHandshakeAsync(
        RpcSession session,
        CancellationToken operationCancellation,
        CancellationToken propagatedCancellation)
    {
        using var handshakeTimeout = new CancellationTokenSource(
            _protocolOptions.HandshakeTimeout,
            _runtimeContext.TimeProvider);
        using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            operationCancellation,
            handshakeTimeout.Token);
        Exception? handshakeException;
        try
        {
            handshakeException = await ProcessHandshakeAsync(session, handshakeCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (handshakeCancellation.IsCancellationRequested)
        {
            handshakeException = new OperationCanceledException(handshakeCancellation.Token);
        }

        if (handshakeException is OperationCanceledException)
        {
            if (propagatedCancellation.IsCancellationRequested)
                throw new OperationCanceledException(propagatedCancellation);
            if (handshakeTimeout.IsCancellationRequested)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.Unavailable,
                    $"RPC handshake timed out after {_protocolOptions.HandshakeTimeout}.");
            }
            throw CreateConnectionClosedException("Client stopped during handshake.");
        }
        if (handshakeException is not null)
            throw handshakeException;
    }

    private void PublishReadyState()
    {
        lock (_stateGate)
        {
            if (_shutdownCts.IsCancellationRequested || ReadyConnectionCount == 0)
                return;
            _readyTimestamp = _runtimeContext.TimeProvider.GetTimestamp();
            TransitionTo(SharpLinkConnectionState.Ready);
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
        PublishReadinessFacts(new ClientReadinessFacts(
            ActiveEndpoints: 1,
            ReadyEndpoints: snapshot.Length == 0 ? 0 : 1,
            ReadyConnections: snapshot.Length,
            TargetReadyEndpoints: 1));
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
        => ex is IOException or ObjectDisposedException or SocketException or
            SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed };

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
            if (!IsTransportFault(ex))
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
            if (!IsTransportFault(ex))
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
            ValidateRemoteContractAssembly(registration);
            var existing = Volatile.Read(ref registration.Proxy);
            if (existing is not null)
                return (T)existing;

            var channel = registration.Module is null
                ? (IRpcChannel)this
                : new SharpLinkModuleRpcChannel(this, registration.Module);
            var created = registration.Descriptor.ProxyFactory(channel, registration.Codecs);
            var published = Interlocked.CompareExchange(ref registration.Proxy, created, null);
            return (T)(published ?? created);
        }

        throw new InvalidOperationException($"Proxy for service interface {typeof(T).FullName} is not registered.");
    }

    public T GetWithMetadata<T>(SharpLinkMetadata metadata) where T : IService
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Count == 0)
            return Get<T>();

        if (Volatile.Read(ref _proxies).TryGetValue(typeof(T), out var registration))
        {
            ValidateRemoteContractAssembly(registration);
            IRpcChannel channel = registration.Module is null
                ? this
                : new SharpLinkModuleRpcChannel(this, registration.Module);
            channel = new SharpLinkMetadataRpcChannel(channel, metadata);
            return (T)registration.Descriptor.ProxyFactory(channel, registration.Codecs);
        }

        throw new InvalidOperationException($"Proxy for service interface {typeof(T).FullName} is not registered.");
    }

    private async Task<Exception?> ProcessHandshakeAsync(RpcSession session, CancellationToken ct)
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
        var compressionProviders = _runtimeContext.Compression.ProviderBindings;
        var negotiationPolicy = ProtocolV2ContractManifestNegotiation.CreateImplementedPolicy(
            _protocolOptions.MaxFramePayloadBytes,
            _runtimeContext.FlowControl.StreamReceiveWindowBytes,
            _runtimeContext.FlowControl.ConnectionReceiveWindowBytes,
            compressionProviders);
        var handshakeRequest = ProtocolV2Negotiator.CreateClientOffer(
            negotiationPolicy,
            ProtocolV2Capabilities.ContractManifest,
            authPayload);
        await session.SendHandshakeRequestAndFlushAsync(handshakeRequest, _protocolOptions, ct).ConfigureAwait(false);

        var reader = session.Input;
        Exception? handshakeException = null;
        NegotiatedSessionOptions? negotiated = null;
        ProtocolV2ContractManifest? manifest = null;
        var handshakeCompleted = false;
        while (session.IsConnected && !ct.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            try
            {
                while (session.IsConnected &&
                       !ct.IsCancellationRequested &&
                       ProtocolV2FrameParser.TryReadFrame(
                           ref buffer, _protocolOptions, out var header, out var payload))
                {
                    SharpLinkTelemetry.RecordReceivedBytes(ProtocolV2Constants.HeaderBytes + payload.Length);
                    session.EnsureInboundFrameAllowed(header.Type);
                    try
                    {
                        if (header.Type == ProtocolV2FrameType.HandshakeResponse)
                        {
                            if (negotiated is not null || manifest is not null)
                            {
                                handshakeException = CreateProtocolViolationException(
                                    "Received duplicate or out-of-order HandshakeResponse.");
                            }
                            else if ((header.Flags & ProtocolV2FrameFlags.Error) == 0)
                            {
                                var response = ProtocolV2PayloadCodec.ReadHandshakeResponse(payload, _protocolOptions);
                                negotiated = ProtocolV2Negotiator.ValidateServerResponse(
                                    handshakeRequest,
                                    response,
                                    negotiationPolicy);
                                if (!session.TryCompleteHandshake(negotiated))
                                {
                                    handshakeException = CreateProtocolViolationException(
                                        "The handshake result was already completed or the session terminated.");
                                }
                            }
                            else
                            {
                                var error = ProtocolV2PayloadCodec.ReadError(
                                    payload, header.Flags, _protocolOptions.MaxErrorMessageBytes);
                                handshakeException = new SharpLinkException(
                                    error.Code,
                                    error.DetailCode,
                                    error.Message);
                                if (error.Code is SharpLinkErrorCode.AuthenticationRejected or
                                    SharpLinkErrorCode.AuthenticationExpired or
                                    SharpLinkErrorCode.AuthorizationDenied or
                                    SharpLinkErrorCode.PermissionDenied)
                                {
                                    SharpLinkTelemetry.RecordAuthenticationFailure("client");
                                }
                            }
                        }
                        else if (header.Type == ProtocolV2FrameType.ContractManifest)
                        {
                            if (negotiated is null || manifest is not null)
                            {
                                handshakeException = CreateProtocolViolationException(
                                    "Received duplicate or out-of-order ContractManifest during handshake.");
                            }
                            else
                            {
                                manifest = ProtocolV2ContractManifestCodec.Read(payload, _protocolOptions);
                                PublishRemoteContractManifest(session, manifest);
                                handshakeCompleted = true;
                            }
                        }
                        else
                        {
                            handshakeException = CreateProtocolViolationException(
                                "Received unexpected packet during handshake.");
                        }
                    }
                    catch (SharpLinkException exception)
                    {
                        handshakeException = exception;
                    }

                    if (handshakeException is not null)
                        handshakeCompleted = true;

                    if (handshakeCompleted)
                        break;
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.Start, handshakeCompleted ? buffer.Start : buffer.End);
            }

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
                while (session.IsConnected &&
                       !ct.IsCancellationRequested &&
                       ProtocolV2FrameParser.TryReadFrame(ref buffer, _protocolOptions, out var header, out var payload))
                {
                    SharpLinkTelemetry.RecordReceivedBytes(ProtocolV2Constants.HeaderBytes + payload.Length);
                    session.MarkActive();
                    session.EnsureInboundFrameAllowed(header.Type);
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
                        {
                            connection.PendingCalls.DispatchError(requestId, exception);
                        }
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
                                await session.SendPongWithBackpressureAsync(
                                    ReadMonotonicTimestamp(payload), ct).ConfigureAwait(false);
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
                            case ProtocolV2FrameType.ContractManifest:
                                PublishRemoteContractManifest(
                                    session,
                                    ProtocolV2ContractManifestCodec.Read(payload, _protocolOptions));
                                break;
                            case ProtocolV2FrameType.StreamData:
                                var streamRequestId = unchecked((long)header.RequestId);
                                if (connection.PendingCalls.TryAcceptStreamData(streamRequestId))
                                {
                                    var dispatchTask = DispatchStreamChunkAsync(session, streamRequestId, payload);
                                    if (!dispatchTask.IsCompletedSuccessfully)
                                        await dispatchTask;
                                }
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
                                        new SharpLinkException(
                                            goAwayError.Code,
                                            goAwayError.DetailCode,
                                            goAwayError.Message));
                                break;
                            case ProtocolV2FrameType.HandshakeRequest:
                            case ProtocolV2FrameType.HandshakeResponse:
                            case ProtocolV2FrameType.Request:
                            case ProtocolV2FrameType.HealthCheck:
                            default:
                                SharpLinkTelemetry.RecordProtocolFailure("client");
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
                catch (InvalidOperationException) when (
                    !session.IsConnected || ct.IsCancellationRequested)
                {
                    // Transport teardown can complete a StreamPipeReader after ReadAsync
                    // returns. The buffer is already terminal and has no remaining owner.
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
            await session.SendPingWithBackpressureAsync(ct).ConfigureAwait(false);
            await SharpLinkTimer.DelayAsync(
                _heartbeatInterval,
                _runtimeContext.TimeProvider,
                ct).ConfigureAwait(false);
            if (session.TimeSinceLastActivity <= _heartbeatTimeout && session.IsConnected)
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
            var remoteException = SharpLinkResourceExhaustion.CreateRemote(
                error.Code,
                error.DetailCode,
                error.Message);
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
