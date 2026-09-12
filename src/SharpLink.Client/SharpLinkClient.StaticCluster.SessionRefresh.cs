namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private sealed partial class StaticClusterRuntime
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
            _client.TrackFrameworkTask(task, "StaticClusterSessionRefreshRollout");
        }

        private async Task RunSessionRefreshRolloutAsync(object owner)
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
                        {
                            ReleaseSessionRefreshWorkerLocked(owner);
                            return;
                        }

                        List<ClientConnection>? stale = null;
                        foreach (var pair in _sessionRefreshDebt)
                        {
                            var candidate = pair.Key;
                            var ownerEndpoint = FindEndpointLocked(candidate);
                            if (ownerEndpoint is null || candidate.HasPlannedSessionRefreshRetirement || !candidate.CanAcceptCalls)
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
            for (var endpointIndex = 0; endpointIndex < _endpoints.Length; endpointIndex++)
            {
                foreach (var connection in _endpoints[endpointIndex].Connections)
                {
                    if (connection.HasPlannedSessionRefreshRetirement)
                        planned++;
                }
            }

            if (_options.MaxRetiringConnections == 0)
                return planned == 0;
            return _retiringConnections.Count + planned < _options.MaxRetiringConnections;
        }

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
            var replacementCommitReserved = false;
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
                    endpoint.Configuration.Endpoint.Id);
                var publishedReplacement = replacement;
                var readySession = publishedReplacement.Session;
                readySession.OnDisconnected += exception =>
                {
                    publishedReplacement.ObserveFatalFailureForAdmission();
                    HandleDisconnected(
                        endpoint,
                        publishedReplacement,
                        exception ?? CreateConnectionClosedException("Transport closed."));
                };

                var published = false;
                var sourceGone = false;
                var retryReplacement = false;
                lock (_gate)
                {
                    if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested)
                        throw CreateConnectionClosedException("Client stopped while refreshing a session.");

                    sourceGone = !ReferenceEquals(FindEndpointLocked(source), endpoint) || !source.CanAcceptCalls;
                    if (!sourceGone && !CanPlanRefreshLocked(source))
                    {
                        retryReplacement = true;
                    }
                    else if (!sourceGone)
                    {
                        endpoint.Connections.Add(publishedReplacement);
                        try
                        {
                            _client.ReconcileResponseCompressionPreferenceAfterReadyPublication(readySession);
                        }
                        catch
                        {
                            endpoint.Connections.Remove(publishedReplacement);
                            throw;
                        }

                        readySession.NotifyConnected();
                        _client.TrackFrameworkTask(
                            _client.RunHeartbeatSendLoopAsync(publishedReplacement, sessionCts.Token),
                            "StaticClusterHeartbeatSendLoop");
                        _client.TrackFrameworkTask(
                            _client.RunProcessRequestLoopAsync(publishedReplacement, sessionCts.Token),
                            "StaticClusterProcessRequestLoop");

                        if (!endpoint.Connections.Contains(publishedReplacement) ||
                            !publishedReplacement.CanAcceptCalls ||
                            !publishedReplacement.TryReserveSessionRefreshCommit())
                        {
                            endpoint.Connections.Remove(publishedReplacement);
                            retryReplacement = true;
                        }
                        else
                        {
                            replacementCommitReserved = true;
                            if (!publishedReplacement.TryCommitSessionRefreshRetirement(source))
                            {
                                // A fatal transition linearized before the eligibility cut while this
                                // attempt already held the admission reservation. Roll the replacement
                                // back and keep the healthy source selectable so the debt retries.
                                endpoint.Connections.Remove(publishedReplacement);
                                retryReplacement = true;
                            }
                            else
                            {
                                PublishReadySnapshotLocked();
                                endpoint.MarkReadyTimestamp(_client._runtimeContext.TimeProvider.GetTimestamp());
                                Volatile.Read(ref _client._afterSessionRefreshEligibilitySwapTestHook)?.Invoke();
                                published = true;
                            }
                        }
                    }
                }

                if (replacementCommitReserved)
                {
                    publishedReplacement.ReleaseCallAdmissionReservation();
                    replacementCommitReserved = false;
                }

                if (!published)
                {
                    session = null;
                    await DisposeConnectionAsync(publishedReplacement).ConfigureAwait(false);
                    replacement = null;
                    return sourceGone || !retryReplacement;
                }

                session = null;
                replacement = null;
                TryAdvancePlannedSessionRefreshRetirement(source);
                PublishClientReadiness();
                return true;
            }
            catch (Exception exception)
            {
                if (replacementCommitReserved && replacement is not null)
                    replacement.ReleaseCallAdmissionReservation();
                if (exception is not OperationCanceledException ||
                    (!cancellationToken.IsCancellationRequested && !_client._shutdownCts.IsCancellationRequested))
                {
                    _client.RecordClusterConnectionFailure(failureStage, exception, endpoint.Index);
                }
                await RethrowAfterFailedConnectionCleanupAsync(exception, transport, replacement, session)
                    .ConfigureAwait(false);
                throw new UnreachableException();
            }
            finally
            {
                lock (_gate)
                    endpoint.ConnectingCount--;

                // A just-published replacement can disconnect before this attempt relinquishes
                // ConnectingCount. Reconcile after the count reaches zero so that disconnect cannot
                // lose the only reconnect trigger for an otherwise empty endpoint.
                if (Volatile.Read(ref _stopping) == 0 && !_client._shutdownCts.IsCancellationRequested)
                {
                    EnsureReconnect(endpoint);
                    EnsureMinimumReadyEndpoints();
                }
            }
        }

        public void TryAdvancePlannedSessionRefreshRetirement(ClientConnection source)
        {
            StaticClientRuntimeEndpointState? endpoint;
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
                if (endpoint.Connections.Remove(source))
                {
                    _retiringConnections.Remove(source);
                    PublishReadySnapshotLocked();
                    dispose = true;
                }
            }

            if (dispose)
            {
                _client.TrackFrameworkTask(
                    DisposeConnectionAsync(source),
                    "StaticClusterSessionRefreshRetiredConnectionCleanup");
                EnsureReconnect(endpoint!);
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
