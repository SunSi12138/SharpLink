namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
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
            connectionState.MarkSessionLoopStarted();
            connection = null;
            session.SetTelemetrySide("server");
            session.BindRuntimeContext(_runtimeContext);
            session.ServiceExceptionMapper = (requestId, contractId, methodId, exception) =>
                MapStreamServiceException(
                    connectionState,
                    session,
                    requestId,
                    contractId,
                    methodId,
                    exception);
            await ReplaceConnectionAsync(connectionState).ConfigureAwait(false);
            await HandleSessionLifecycleAsync(connectionState).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedCancellation(exception, cancellationToken))
        {
        }
        finally
        {
            if (connectionState is not null)
            {
                connectionState.MarkSessionLoopCompleted();
                await connectionState.CloseAsync().ConfigureAwait(false);
            }
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
        catch (Exception ex) when (IsExpectedConnectionTermination(ex, ct))
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
            // Closing a session completes its PipeReader. Publish that this loop no longer
            // owns a ReadResult before any concurrent stop path is allowed to dispose it.
            connection.MarkSessionLoopCompleted();
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
                ObserveRetiredConnectionCleanup(connection);
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
                ObserveRetiredConnectionCleanup(connection);
        }
    }

    private void ObserveRetiredConnectionCleanup(ServerConnectionState connection)
    {
        var cleanup = CompleteRetiredConnectionCleanupAsync(connection);
        if (connection.ActiveCalls == 0)
            TrackFrameworkTask(cleanup);
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
            await SharpLinkTimer.DelayAsync(heartbeatCheckInterval, ct).ConfigureAwait(false);
            foreach (var (id, connection) in _connections)
            {
                var session = connection.Session;
                if (session.TimeSinceLastActivity <= heartbeatTimeout || !session.IsConnected)
                    continue;

                using var sessionScope = BeginSessionLogScope(_logger, session.Id);
                LogClientHeartbeatTimeout(_logger);

                if (_connections.TryGetValue(id, out var current) && ReferenceEquals(current, connection))
                    await DisconnectConnectionAsync(connection).ConfigureAwait(false);
            }
        }
    }
}
