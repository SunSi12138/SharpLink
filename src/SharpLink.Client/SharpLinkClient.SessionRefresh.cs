namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private readonly Dictionary<ClientConnection, ProtocolV2SessionRefreshRequested> _sessionRefreshDebt = [];
    private Task? _sessionRefreshTask;

    private void HandleSessionRefreshRequest(
        RpcSession session,
        ProtocolV2SessionRefreshRequested request)
    {
        if (_cluster is not null)
        {
            _cluster.RequestSessionRefresh(session, request);
            return;
        }

        // Anonymous-pipe offers are one-shot. The server-side negotiation gate prevents this
        // capability from being selected for them; retain this defensive check so a custom peer
        // cannot turn an administrative request into a reconnect loop.
        if (transportFactory is AnonymousPipeClientTransportFactory)
            return;

        lock (_poolGate)
        {
            if (_poolStopping || _shutdownCts.IsCancellationRequested || Volatile.Read(ref _stopStarted) != 0)
                return;

            ClientConnection? source = null;
            foreach (var connection in _connections)
            {
                if (ReferenceEquals(connection.Session, session))
                {
                    source = connection;
                    break;
                }
            }
            if (source is null || !source.CanAcceptCalls)
                return;

            if (_sessionRefreshDebt.TryGetValue(source, out var previous) &&
                previous.ServerInstanceId == request.ServerInstanceId &&
                request.DesiredGeneration <= previous.DesiredGeneration)
            {
                return;
            }

            _sessionRefreshDebt[source] = request;
            if (_sessionRefreshTask is { IsCompleted: false })
                return;

            _sessionRefreshTask = RunFixedSessionRefreshRolloutAsync();
            TrackFrameworkTask(_sessionRefreshTask, "SessionRefreshRollout");
        }
    }

    private async Task RunFixedSessionRefreshRolloutAsync()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                ClientConnection? source = null;
                lock (_poolGate)
                {
                    if (_poolStopping || Volatile.Read(ref _stopStarted) != 0)
                        return;

                    List<ClientConnection>? stale = null;
                    foreach (var pair in _sessionRefreshDebt)
                    {
                        var candidate = pair.Key;
                        if (!_connections.Contains(candidate) || !candidate.CanAcceptCalls)
                        {
                            (stale ??= []).Add(candidate);
                            continue;
                        }
                        if (source is null && CanPlanFixedRefreshLocked(candidate))
                            source = candidate;
                    }
                    if (stale is not null)
                    {
                        for (var index = 0; index < stale.Count; index++)
                            _sessionRefreshDebt.Remove(stale[index]);
                    }
                    if (_sessionRefreshDebt.Count == 0)
                        return;
                }

                if (source is null)
                {
                    await DelaySessionRefreshRetryAsync().ConfigureAwait(false);
                    continue;
                }

                try
                {
                    // Small client-side jitter avoids turning a server broadcast into a perfectly
                    // synchronized reconnect wave while keeping the old session fully Ready.
                    await Task.Delay(Random.Shared.Next(10, 76), _shutdownCts.Token).ConfigureAwait(false);
                    var completed = await ReplaceFixedSessionAsync(source, _shutdownCts.Token)
                        .ConfigureAwait(false);
                    if (completed)
                    {
                        lock (_poolGate)
                            _sessionRefreshDebt.Remove(source);
                        continue;
                    }
                }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    using var scope = BeginSessionLogScope(_logger, source.Session.Id);
                    LogClientConnectionAttemptFailed(
                        _logger,
                        nameof(RunFixedSessionRefreshRolloutAsync),
                        exception);
                }

                await DelaySessionRefreshRetryAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_poolGate)
                _sessionRefreshTask = null;
        }
    }

    private bool CanPlanFixedRefreshLocked(ClientConnection source)
    {
        if (source.ActiveCallCount == 0)
            return true;

        var retiring = 0;
        foreach (var connection in _connections)
        {
            if (connection.State == ClientConnectionState.Draining)
                retiring++;
        }
        return retiring < _connectionPoolOptions.MaxConnections;
    }

    private async Task<bool> ReplaceFixedSessionAsync(
        ClientConnection source,
        CancellationToken cancellationToken)
    {
        lock (_poolGate)
        {
            if (_poolStopping || _shutdownCts.IsCancellationRequested || Volatile.Read(ref _stopStarted) != 0)
                return true;
            if (!_connections.Contains(source) || !source.CanAcceptCalls)
                return true;
            if (!CanPlanFixedRefreshLocked(source))
                return false;
        }

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCts.Token);
        RpcSession? session = null;
        ITransportConnection? transport = null;
        ClientConnection? replacement = null;
        try
        {
            transport = await transportFactory.ConnectAsync(attemptCts.Token).ConfigureAwait(false);
            if (transport is ITransportSecurityInfo securityInfo)
                LogTlsEstablished(_logger, securityInfo.Protocol, securityInfo.CipherSuite);
            session = new RpcSession(
                transport,
                new RpcSessionCreationOptions(
                    RpcSessionRole.Client,
                    _runtimeContext,
                    _rpcSessionFlushOptions,
                    _requestCompressionPolicy));
            transport = null;

            await CompleteHandshakeAsync(session, attemptCts.Token, cancellationToken).ConfigureAwait(false);
            if (_beforeReadyPublicationTestHook is not null)
                await _beforeReadyPublicationTestHook(attemptCts.Token).ConfigureAwait(false);

            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            replacement = new ClientConnection(
                this,
                session,
                sessionCts,
                _protocolOptions.MaxPendingRequestsPerConnection,
                _runtimeContext);
            var readySession = replacement.Session;
            readySession.OnDisconnected += exception => HandleDisconnected(
                replacement,
                exception ?? CreateConnectionClosedException("Transport closed."));

            var publishReplacement = false;
            var sourceStillEligible = false;
            lock (_poolGate)
            {
                if (_poolStopping || _shutdownCts.IsCancellationRequested || Volatile.Read(ref _stopStarted) != 0)
                    throw CreateConnectionClosedException("Client stopped while refreshing a session.");

                sourceStillEligible = _connections.Contains(source) && source.CanAcceptCalls;
                if (sourceStillEligible && CanPlanFixedRefreshLocked(source))
                {
                    _connections.Add(replacement);
                    try
                    {
                        // Reconcile connection-local control state before the atomic eligibility
                        // swap. No business call can select the replacement until the snapshot below.
                        ReconcileResponseCompressionPreferenceAfterReadyPublication(readySession);
                    }
                    catch
                    {
                        _connections.Remove(replacement);
                        throw;
                    }

                    if (!source.MarkDraining())
                    {
                        _connections.Remove(replacement);
                        throw new InvalidOperationException(
                            "The refresh source stopped being Ready before replacement publication.");
                    }

                    PublishReadySnapshotLocked();
                    readySession.NotifyConnected();
                    TrackFrameworkTask(
                        RunHeartbeatSendLoopAsync(replacement, sessionCts.Token),
                        "HeartbeatSendLoop");
                    TrackFrameworkTask(
                        RunProcessRequestLoopAsync(replacement, sessionCts.Token),
                        "ProcessRequestLoop");
                    publishReplacement = true;
                }
            }

            if (!publishReplacement)
            {
                session = null;
                await replacement.DisposeAsync().ConfigureAwait(false);
                replacement = null;
                return !sourceStillEligible;
            }

            session = null;
            replacement = null;
            RetireDrainingConnectionIfIdle(source);
            PublishReadyState();
            return true;
        }
        catch (Exception exception)
        {
            await RethrowAfterFailedConnectionCleanupAsync(
                exception,
                transport,
                replacement,
                session).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }

    private async Task DelaySessionRefreshRetryAsync()
    {
        try
        {
            await Task.Delay(Random.Shared.Next(250, 751), _shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
    }
}
