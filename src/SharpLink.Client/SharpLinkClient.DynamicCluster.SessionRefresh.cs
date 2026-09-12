namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private sealed partial class DynamicClusterRuntime
    {
        private readonly Dictionary<ClientConnection, ProtocolV2SessionRefreshRequested> _sessionRefreshDebt = [];
        private object? _sessionRefreshWorker;
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
                EnsureSessionRefreshWorkerLocked();
            }
        }

        private void EnsureSessionRefreshWorkerLocked()
        {
            if (_sessionRefreshWorker is not null)
                return;
            var owner = new object();
            _sessionRefreshWorker = owner;
            var task = RunSessionRefreshRolloutAsync(owner);
            _sessionRefreshTask = task;
            _lifecycle.TrackTask(task, "DynamicClusterSessionRefreshRollout");
        }

        private async Task RunSessionRefreshRolloutAsync(object owner)
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
                        {
                            ReleaseSessionRefreshWorkerLocked(owner);
                            return;
                        }

                        List<ClientConnection>? stale = null;
                        foreach (var pair in _sessionRefreshDebt)
                        {
                            var candidate = pair.Key;
                            var ownerEndpoint = FindEndpointLocked(candidate);
                            if (ownerEndpoint is null || ownerEndpoint.Retiring || !IsCurrentLocked(ownerEndpoint) ||
                                candidate.HasPlannedSessionRefreshRetirement || !candidate.CanAcceptCalls)
                            {
                                (stale ??= []).Add(candidate);
                                continue;
                            }
                            if (source is null && CanPlanRefreshLocked(candidate))
                            {
                                source = candidate;
                                endpoint = ownerEndpoint;
                            }
                        }
                        if (stale is not null)
                        {
                            for (var index = 0; index < stale.Count; index++)
                                _sessionRefreshDebt.Remove(stale[index]);
                        }
                        if (_sessionRefreshDebt.Count == 0)
                        {
                            Volatile.Read(ref _client._beforeSessionRefreshWorkerReleaseTestHook)?.Invoke();
                            ReleaseSessionRefreshWorkerLocked(owner);
                            return;
                        }
                    }

                    if (source is null || endpoint is null)
                    {
                        await DelayRefreshRetryAsync().ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        await Task.Delay(Random.Shared.Next(10, 76), _client._shutdownCts.Token).ConfigureAwait(false);
                        var completed = await ReplaceSessionAsync(source, endpoint, _client._shutdownCts.Token)
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
                    ReleaseSessionRefreshWorkerLocked(owner);
            }
        }

        private void ReleaseSessionRefreshWorkerLocked(object owner)
        {
            if (!ReferenceEquals(_sessionRefreshWorker, owner))
                return;
            _sessionRefreshWorker = null;
            _sessionRefreshTask = null;
        }

        private bool CanPlanRefreshLocked(ClientConnection source)
        {
            if (source.ActiveCallCount == 0)
                return true;

            var planned = 0;
            foreach (var state in _current.States)
            {
                foreach (var connection in _connections.GetOwnedConnections(state))
                {
                    if (connection.HasPlannedSessionRefreshRetirement)
                        planned++;
                }
            }

            if (_options.MaxRetiringConnections == 0)
                return planned == 0;
            return _connections.RetiringConnectionCount + planned < _options.MaxRetiringConnections;
        }

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
                transport = await _client.ConnectTransportAsync(
                    endpoint.Configuration.TransportFactory,
                    attemptCts.Token).ConfigureAwait(false);
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
                await _client.CompleteHandshakeAsync(session, attemptCts.Token, cancellationToken).ConfigureAwait(false);

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
                var publishedReplacement = replacement;
                var readySession = publishedReplacement.Session;
                readySession.OnDisconnected += exception => HandleDisconnected(
                    endpoint,
                    publishedReplacement,
                    exception ?? CreateConnectionClosedException("Transport closed."));

                var published = false;
                var sourceGone = false;
                var retryForCapacity = false;
                lock (_gate)
                {
                    if (_lifecycle.IsStopping || _client._shutdownCts.IsCancellationRequested)
                        throw CreateConnectionClosedException("Client stopped while refreshing a session.");

                    sourceGone = endpoint.Retiring || !IsCurrentLocked(endpoint) ||
                                 !ReferenceEquals(FindEndpointLocked(source), endpoint) || !source.CanAcceptCalls;
                    if (!sourceGone && !CanPlanRefreshLocked(source))
                    {
                        retryForCapacity = true;
                    }
                    else if (!sourceGone)
                    {
                        _connections.Add(endpoint, publishedReplacement);
                        try
                        {
                            _client.ReconcileResponseCompressionPreferenceAfterReadyPublication(readySession);
                        }
                        catch
                        {
                            _connections.Remove(endpoint, publishedReplacement);
                            throw;
                        }

                        readySession.NotifyConnected();
                        _lifecycle.TrackTask(
                            _client.RunHeartbeatSendLoopAsync(publishedReplacement, sessionCts.Token),
                            "DynamicClusterHeartbeatSendLoop");
                        _lifecycle.TrackTask(
                            _client.RunProcessRequestLoopAsync(publishedReplacement, sessionCts.Token),
                            "DynamicClusterProcessRequestLoop");

                        source.BeginPlannedSessionRefreshRetirement(publishedReplacement);
                        PublishReadySnapshotLocked();
                        endpoint.MarkReadyTimestamp(_client._runtimeContext.TimeProvider.GetTimestamp());
                        Volatile.Read(ref _client._afterSessionRefreshEligibilitySwapTestHook)?.Invoke();
                        published = true;
                    }
                }

                if (!published)
                {
                    session = null;
                    await DynamicClusterRuntimeLifecycle.DisposeConnectionAsync(publishedReplacement).ConfigureAwait(false);
                    replacement = null;
                    return sourceGone || !retryForCapacity;
                }

                session = null;
                replacement = null;
                TryAdvancePlannedSessionRefreshRetirement(source);
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
                await RethrowAfterFailedConnectionCleanupAsync(exception, transport, replacement, session)
                    .ConfigureAwait(false);
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

                // A replacement can disconnect after publication but before this attempt clears
                // ConnectingCount. Reconcile once the count is zero so the disconnect callback's
                // earlier no-op cannot strand a current endpoint with no Ready connection.
                if (!_lifecycle.IsStopping && !_client._shutdownCts.IsCancellationRequested)
                {
                    _reconnect.EnsureReconnect(endpoint);
                    _reconnect.EnsureMinimumReadyEndpoints();
                }
            }
        }

        public void TryAdvancePlannedSessionRefreshRetirement(ClientConnection source)
        {
            DynamicEndpointState? endpoint;
            var dispose = false;
            lock (_gate)
            {
                if (!source.HasPlannedSessionRefreshRetirement)
                    return;
                endpoint = FindEndpointLocked(source);
                if (endpoint is null)
                {
                    source.CompletePlannedSessionRefreshRetirement();
                    return;
                }
                if (source.CallAdmissionReservationCount != 0 || source.ActiveCallCount != 0)
                    return;

                source.CompletePlannedSessionRefreshRetirement();
                _ = source.MarkDraining();
                if (_connections.Remove(endpoint, source))
                {
                    PublishReadySnapshotLocked();
                    dispose = true;
                }
            }

            if (dispose)
            {
                _lifecycle.TrackTask(
                    DynamicClusterRuntimeLifecycle.DisposeConnectionAsync(source),
                    "DynamicClusterSessionRefreshRetiredConnectionCleanup");
                if (!endpoint!.Retiring)
                    _reconnect.EnsureReconnect(endpoint);
            }
        }

        private async Task DelayRefreshRetryAsync()
        {
            try
            {
                await Task.Delay(Random.Shared.Next(250, 751), _client._shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
            {
            }
        }
    }
}
