namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private sealed partial class DynamicClusterRuntime
    {
        private readonly Dictionary<ClientConnection, ProtocolV2SessionRefreshRequested> _sessionRefreshDebt = [];
        private Task? _sessionRefreshTask;

        public void RequestSessionRefresh(
            RpcSession session,
            ProtocolV2SessionRefreshRequested request)
        {
            lock (_gate)
            {
                if (_lifecycle.IsStopping || _client._shutdownCts.IsCancellationRequested)
                    return;

                ClientConnection? source = null;
                var states = _current.States;
                for (var stateIndex = 0; stateIndex < states.Count && source is null; stateIndex++)
                {
                    foreach (var connection in _connections.GetOwnedConnections(states[stateIndex]))
                    {
                        if (ReferenceEquals(connection.Session, session))
                        {
                            source = connection;
                            break;
                        }
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

                _sessionRefreshTask = RunSessionRefreshRolloutAsync();
                _lifecycle.TrackTask(_sessionRefreshTask, "DynamicClusterSessionRefreshRollout");
            }
        }

        private async Task RunSessionRefreshRolloutAsync()
        {
            try
            {
                while (!_client._shutdownCts.IsCancellationRequested)
                {
                    ClientConnection? source = null;
                    DynamicEndpointState? endpoint = null;
                    lock (_gate)
                    {
                        if (_lifecycle.IsStopping)
                            return;

                        List<ClientConnection>? stale = null;
                        foreach (var pair in _sessionRefreshDebt)
                        {
                            var candidate = pair.Key;
                            var owner = FindEndpointLocked(candidate);
                            if (owner is null || owner.Retiring || !IsCurrentLocked(owner) || !candidate.CanAcceptCalls)
                            {
                                (stale ??= []).Add(candidate);
                                continue;
                            }
                            if (source is null && CanPlanRefreshLocked(candidate))
                            {
                                source = candidate;
                                endpoint = owner;
                            }
                        }
                        if (stale is not null)
                        {
                            for (var index = 0; index < stale.Count; index++)
                                _sessionRefreshDebt.Remove(stale[index]);
                        }
                        if (_sessionRefreshDebt.Count == 0)
                            return;
                    }

                    if (source is null || endpoint is null)
                    {
                        await DelayRefreshRetryAsync().ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        await Task.Delay(Random.Shared.Next(10, 76), _client._shutdownCts.Token)
                            .ConfigureAwait(false);
                        var completed = await ReplaceSessionAsync(
                                source,
                                endpoint,
                                _client._shutdownCts.Token)
                            .ConfigureAwait(false);
                        if (completed)
                        {
                            lock (_gate)
                                _sessionRefreshDebt.Remove(source);
                            continue;
                        }
                    }
                    catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        LogClientConnectionAttemptFailed(
                            _client._logger,
                            nameof(RunSessionRefreshRolloutAsync),
                            exception);
                    }

                    await DelayRefreshRetryAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_gate)
                    _sessionRefreshTask = null;
            }
        }

        private bool CanPlanRefreshLocked(ClientConnection source)
            => source.ActiveCallCount == 0 ||
               (_options.MaxRetiringConnections != 0 &&
                _connections.RetiringConnectionCount < _options.MaxRetiringConnections);

        private async Task<bool> ReplaceSessionAsync(
            ClientConnection source,
            DynamicEndpointState endpoint,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_lifecycle.IsStopping || _client._shutdownCts.IsCancellationRequested)
                    return true;
                if (endpoint.Retiring || !IsCurrentLocked(endpoint) ||
                    !ReferenceEquals(FindEndpointLocked(source), endpoint) || !source.CanAcceptCalls)
                    return true;
                if (!CanPlanRefreshLocked(source))
                    return false;
                endpoint.ConnectingCount++;
            }

            RpcSession? session = null;
            ITransportConnection? transport = null;
            ClientConnection? replacement = null;
            var failureStage = SharpLinkConnectionFailureStage.Dial;
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _client._shutdownCts.Token);
                transport = await endpoint.Configuration.TransportFactory.ConnectAsync(attemptCts.Token)
                    .ConfigureAwait(false);
                if (transport is ITransportSecurityInfo securityInfo)
                    LogTlsEstablished(_client._logger, securityInfo.Protocol, securityInfo.CipherSuite);

                failureStage = SharpLinkConnectionFailureStage.Handshake;
                session = new RpcSession(
                    transport,
                    new RpcSessionCreationOptions(
                        RpcSessionRole.Client,
                        _client._runtimeContext,
                        _client._rpcSessionFlushOptions,
                        _client._requestCompressionPolicy));
                transport = null;
                await _client.CompleteHandshakeAsync(session, attemptCts.Token, cancellationToken)
                    .ConfigureAwait(false);

                failureStage = SharpLinkConnectionFailureStage.Readiness;
                if (_client._beforeReadyPublicationTestHook is not null)
                    await _client._beforeReadyPublicationTestHook(attemptCts.Token).ConfigureAwait(false);

                var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_client._shutdownCts.Token);
                replacement = new ClientConnection(
                    _client,
                    session,
                    sessionCts,
                    _client._protocolOptions.MaxPendingRequestsPerConnection,
                    _client._runtimeContext,
                    endpoint.Configuration.Endpoint.Id,
                    endpoint.Generation);
                var readySession = replacement.Session;
                readySession.OnDisconnected += exception => HandleDisconnected(
                    endpoint,
                    replacement,
                    exception ?? CreateConnectionClosedException("Transport closed."));

                var published = false;
                var sourceGone = false;
                var retryForRetiringCapacity = false;
                var disposeOld = false;
                lock (_gate)
                {
                    if (_lifecycle.IsStopping || _client._shutdownCts.IsCancellationRequested)
                        throw CreateConnectionClosedException("Client stopped while refreshing a session.");

                    sourceGone = endpoint.Retiring || !IsCurrentLocked(endpoint) ||
                                 !ReferenceEquals(FindEndpointLocked(source), endpoint) || !source.CanAcceptCalls;
                    if (!sourceGone && !CanPlanRefreshLocked(source))
                    {
                        retryForRetiringCapacity = true;
                    }
                    else if (!sourceGone)
                    {
                        _connections.Add(endpoint, replacement);
                        try
                        {
                            _client.ReconcileResponseCompressionPreferenceAfterReadyPublication(readySession);
                        }
                        catch
                        {
                            _connections.Remove(endpoint, replacement);
                            throw;
                        }

                        if (!_connections.TryMarkDraining(source, out var retiredOwner, out disposeOld) ||
                            !ReferenceEquals(retiredOwner, endpoint))
                        {
                            _connections.Remove(endpoint, replacement);
                            throw new InvalidOperationException(
                                "The refresh source lost endpoint ownership before replacement publication.");
                        }

                        PublishReadySnapshotLocked();
                        endpoint.MarkReadyTimestamp(_client._runtimeContext.TimeProvider.GetTimestamp());
                        readySession.NotifyConnected();
                        _lifecycle.TrackTask(
                            _client.RunHeartbeatSendLoopAsync(replacement, sessionCts.Token),
                            "DynamicClusterHeartbeatSendLoop");
                        _lifecycle.TrackTask(
                            _client.RunProcessRequestLoopAsync(replacement, sessionCts.Token),
                            "DynamicClusterProcessRequestLoop");
                        published = true;
                    }
                }

                if (!published)
                {
                    session = null;
                    await DynamicClusterRuntimeLifecycle.DisposeConnectionAsync(replacement).ConfigureAwait(false);
                    replacement = null;
                    return sourceGone || !retryForRetiringCapacity;
                }

                session = null;
                replacement = null;
                if (disposeOld)
                {
                    _lifecycle.TrackTask(
                        DynamicClusterRuntimeLifecycle.DisposeConnectionAsync(source),
                        "DynamicClusterRefreshRetiredConnectionCleanup");
                }
                UpdateClientReadiness();
                return true;
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException ||
                    (!cancellationToken.IsCancellationRequested && !_client._shutdownCts.IsCancellationRequested))
                {
                    _client.RecordClusterConnectionFailure(failureStage, exception, endpoint.Generation);
                }
                await RethrowAfterFailedConnectionCleanupAsync(
                    exception,
                    transport,
                    replacement,
                    session).ConfigureAwait(false);
                throw new UnreachableException();
            }
            finally
            {
                lock (_gate)
                {
                    endpoint.ConnectingCount--;
                    if (endpoint.Retiring && _connections.CanRelease(endpoint))
                        ScheduleRetiredStateReleaseLocked(endpoint);
                }
            }
        }

        private async Task DelayRefreshRetryAsync()
        {
            try
            {
                await Task.Delay(Random.Shared.Next(250, 751), _client._shutdownCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
            {
            }
        }
    }
}
