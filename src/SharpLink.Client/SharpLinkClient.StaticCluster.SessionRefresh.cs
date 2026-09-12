namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private sealed partial class StaticClusterRuntime
    {
        private readonly Dictionary<ClientConnection, ProtocolV2SessionRefreshRequested> _sessionRefreshDebt = [];
        private Task? _sessionRefreshTask;

        public void RequestSessionRefresh(
            RpcSession session,
            ProtocolV2SessionRefreshRequested request)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested)
                    return;

                ClientConnection? source = null;
                for (var endpointIndex = 0; endpointIndex < _endpoints.Length && source is null; endpointIndex++)
                {
                    foreach (var connection in _endpoints[endpointIndex].Connections)
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
                _client.TrackFrameworkTask(_sessionRefreshTask, "StaticClusterSessionRefreshRollout");
            }
        }

        private async Task RunSessionRefreshRolloutAsync()
        {
            try
            {
                while (!_client._shutdownCts.IsCancellationRequested)
                {
                    ClientConnection? source = null;
                    StaticClientRuntimeEndpointState? endpoint = null;
                    lock (_gate)
                    {
                        if (Volatile.Read(ref _stopping) != 0)
                            return;

                        List<ClientConnection>? stale = null;
                        foreach (var pair in _sessionRefreshDebt)
                        {
                            var candidate = pair.Key;
                            var owner = FindEndpointLocked(candidate);
                            if (owner is null || !candidate.CanAcceptCalls)
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
                _retiringConnections.Count < _options.MaxRetiringConnections);

        private async Task<bool> ReplaceSessionAsync(
            ClientConnection source,
            StaticClientRuntimeEndpointState endpoint,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested)
                    return true;
                if (!ReferenceEquals(FindEndpointLocked(source), endpoint) || !source.CanAcceptCalls)
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
                    endpoint.Configuration.Endpoint.Id);
                var readySession = replacement.Session;
                readySession.OnDisconnected += exception => HandleDisconnected(
                    endpoint,
                    replacement,
                    exception ?? CreateConnectionClosedException("Transport closed."));

                var published = false;
                var sourceGone = false;
                var retryForRetiringCapacity = false;
                var retireImmediately = false;
                lock (_gate)
                {
                    if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested)
                        throw CreateConnectionClosedException("Client stopped while refreshing a session.");

                    sourceGone = !ReferenceEquals(FindEndpointLocked(source), endpoint) || !source.CanAcceptCalls;
                    if (!sourceGone && !CanPlanRefreshLocked(source))
                    {
                        retryForRetiringCapacity = true;
                    }
                    else if (!sourceGone)
                    {
                        endpoint.Connections.Add(replacement);
                        try
                        {
                            _client.ReconcileResponseCompressionPreferenceAfterReadyPublication(readySession);
                        }
                        catch
                        {
                            endpoint.Connections.Remove(replacement);
                            throw;
                        }

                        if (!source.MarkDraining())
                        {
                            endpoint.Connections.Remove(replacement);
                            throw new InvalidOperationException(
                                "The refresh source stopped being Ready before replacement publication.");
                        }
                        if (source.ActiveCallCount == 0)
                        {
                            endpoint.Connections.Remove(source);
                            retireImmediately = true;
                        }
                        else
                        {
                            _retiringConnections.Add(source);
                        }

                        PublishReadySnapshotLocked();
                        endpoint.MarkReadyTimestamp(_client._runtimeContext.TimeProvider.GetTimestamp());
                        readySession.NotifyConnected();
                        _client.TrackFrameworkTask(
                            _client.RunHeartbeatSendLoopAsync(replacement, sessionCts.Token),
                            "StaticClusterHeartbeatSendLoop");
                        _client.TrackFrameworkTask(
                            _client.RunProcessRequestLoopAsync(replacement, sessionCts.Token),
                            "StaticClusterProcessRequestLoop");
                        published = true;
                    }
                }

                if (!published)
                {
                    session = null;
                    await DisposeConnectionAsync(replacement).ConfigureAwait(false);
                    replacement = null;
                    return sourceGone || !retryForRetiringCapacity;
                }

                session = null;
                replacement = null;
                if (retireImmediately)
                {
                    _client.TrackFrameworkTask(
                        DisposeConnectionAsync(source),
                        "StaticClusterRefreshRetiredConnectionCleanup");
                }
                PublishClientReadiness();
                return true;
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException ||
                    (!cancellationToken.IsCancellationRequested && !_client._shutdownCts.IsCancellationRequested))
                {
                    _client.RecordClusterConnectionFailure(failureStage, exception, endpoint.Index);
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
                    endpoint.ConnectingCount--;
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
