namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private async Task HandleAcceptedConnectionAsync(
        ITransportConnection acceptedConnection,
        ServerConnectionAdmission.Lease connectionLease,
        CancellationToken cancellationToken)
    {
        ITransportConnection? connection = acceptedConnection;
        ServerConnectionState? connectionState = null;
        try
        {
            // The handshake slot covers TLS, the Protocol v2 handshake, and application
            // authentication. It is released exactly once: at the Ready transition, or by
            // the terminal cleanup below when the connection fails before Ready.
            if (!_connectionAdmission.TryAcquireHandshake(connectionLease))
            {
                RecordConnectionAdmissionRejection(ConnectionAdmissionRejectionReason.HandshakeLimit);
                return;
            }

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

            var callCancellations = new StripedLongMap<ServerCallCancellationState>(
                _runtimeContext.Concurrency);
            var session = new RpcSession(
                connection,
                new RpcSessionCreationOptions(
                    RpcSessionRole.Server,
                    _runtimeContext,
                    _rpcSessionFlushOptions));
            var generatedBridge = new ServerGeneratedBridge(this, session, callCancellations);
            connectionState = new ServerConnectionState(
                session,
                generatedBridge,
                callCancellations,
                cancellationToken,
                _runtimeContext.TimeProvider,
                _maxConcurrentCallsPerConnection);
            connectionState.MarkSessionLoopStarted();
            connection = null;
            await ReplaceConnectionAsync(connectionState).ConfigureAwait(false);
            await HandleSessionLifecycleAsync(connectionState, connectionLease).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedCancellation(exception, cancellationToken))
        {
        }
        finally
        {
            try
            {
                if (connectionState is not null)
                {
                    connectionState.MarkSessionLoopCompleted();
                    await connectionState.CloseAsync().ConfigureAwait(false);
                }
                else if (connection is not null)
                    await connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // Released only after terminal cleanup: a slow disposal must not hand the
                // slot to a new connection while the previous transport and framework
                // task are still live. The Ready transition already released the
                // handshake slot, so its release here is a no-op for Ready connections.
                connectionLease.ReleaseHandshake();
                connectionLease.ReleaseConnection();
            }
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
    }

    private async Task HandleSessionLifecycleAsync(
        ServerConnectionState connection,
        ServerConnectionAdmission.Lease connectionLease)
    {
        var session = connection.Session;
        var ct = connection.ConnectionToken;
        var hasConnected = false;
        using var sessionScope = BeginSessionLogScope(_logger, session.Id);
        try
        {
            using var handshakeTimeoutCts = new CancellationTokenSource(
                _protocolOptions.HandshakeTimeout,
                _runtimeContext.TimeProvider);
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
                // Protocol-violation rejections already emitted their bounded classified
                // Warning inside ProcessHandshakeAsync; logging the generic handshake
                // failure here too would let hostile input grow the log per connection.
                if (authResult.ErrorCode != SharpLinkErrorCode.ProtocolViolation)
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

            // The handshake (TLS + Protocol v2 + authentication) is complete: release the
            // handshake slot while the connection slot follows the full connection lifetime.
            connectionLease.ReleaseHandshake();

            hasConnected = true;
            session.NotifyConnected();
            LogClientConnected(_logger);
            await ProcessRequestLoop(connection);
        }
        catch (Exception ex) when (IsExpectedConnectionTermination(ex, ct))
        {
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
            SharpLinkTelemetry.RecordProtocolFailure("server");
            if (SharpLinkProtocolViolationException.Classify(exception) ==
                ProtocolViolationReason.InternalState)
            {
                // A server-side invariant break is a real Server bug: keep the Error path
                // with the full exception and stack trace instead of masking it as a
                // bounded hostile-input Warning.
                LogServerBackgroundLoopUnhandledException(
                    _logger,
                    nameof(ProcessRequestLoop),
                    exception);
                return;
            }

            // A ProtocolViolation is hostile or invalid wire input: count it, emit at most
            // one bounded Warning per throttle window, and never attach the exception
            // (payload, stack trace) to the log.
            LogProtocolViolationRateLimited(
                SharpLinkProtocolViolationException.Classify(exception));
        }
        catch (Exception ex)
        {
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

    /// <summary>
    /// Emits at most one ProtocolViolation Warning per fixed window, with an optional
    /// suppressed-count line, while the violation itself is always telemetry-counted by
    /// the caller. Suppressed events never touch the logger.
    /// </summary>
    private void LogProtocolViolationRateLimited(ProtocolViolationReason reason)
    {
        if (!_protocolViolationLogThrottle.ShouldLog(
                _runtimeContext.TimeProvider.GetTimestamp(),
                out var suppressedCount))
        {
            return;
        }

        if (suppressedCount > 0)
            LogProtocolViolationSuppressed(_logger, suppressedCount);
        LogProtocolViolation(_logger, reason.ToLogToken());
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
            {
                if (connection.ActiveCalls == 0)
                    await CompleteRetiredConnectionCleanupAsync(connection).ConfigureAwait(false);
                else
                    ObserveDeferredRetiredConnectionCleanup(connection);
            }
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
            {
                if (connection.ActiveCalls == 0)
                    await CompleteRetiredConnectionCleanupAsync(connection).ConfigureAwait(false);
                else
                    ObserveDeferredRetiredConnectionCleanup(connection);
            }
        }
    }

    private void ObserveDeferredRetiredConnectionCleanup(ServerConnectionState connection)
    {
        Interlocked.Increment(ref _deferredConnectionCleanups);
        _ = ObserveDeferredRetiredConnectionCleanupAsync(connection);
    }

    private async Task ObserveDeferredRetiredConnectionCleanupAsync(ServerConnectionState connection)
    {
        try
        {
            await CompleteRetiredConnectionCleanupAsync(connection).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDeferredCleanupFailed(_logger, "ConnectionServices", exception);
        }
        finally
        {
            Interlocked.Decrement(ref _deferredConnectionCleanups);
        }
    }

    private async Task CompleteRetiredConnectionCleanupAsync(ServerConnectionState connection)
    {
        try
        {
            await connection.ServiceCleanupTask.ConfigureAwait(false);
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
            await SharpLinkTimer.DelayAsync(
                _heartbeatCheckInterval,
                _runtimeContext.TimeProvider,
                ct).ConfigureAwait(false);
            foreach (var (id, connection) in _connections)
            {
                var session = connection.Session;
                if (session.TimeSinceLastActivity <= _heartbeatTimeout || !session.IsConnected)
                    continue;

                using var sessionScope = BeginSessionLogScope(_logger, session.Id);
                LogClientHeartbeatTimeout(_logger);

                if (_connections.TryGetValue(id, out var current) && ReferenceEquals(current, connection))
                    await DisconnectConnectionAsync(connection).ConfigureAwait(false);
            }
        }
    }
}
