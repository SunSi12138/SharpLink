namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Orchestrates resolver topology, connection ownership and focused reconnect/lifecycle collaborators
    /// for a dynamic endpoint cluster.
    /// </summary>
    private sealed class DynamicClusterRuntime : IEndpointClusterRuntime
    {
        private readonly SharpLinkClient _client;
        private readonly ISharpLinkEndpointResolver _resolver;
        private readonly SharpLinkEndpointTransportFactory _transportFactory;
        private readonly SharpLinkClusterOptions _options;
        private readonly DynamicClusterTopologyState _current;
        private readonly DynamicClusterConnectionState _connections = new();
        private readonly Lock _gate = new();
        private readonly DynamicClusterRuntimeLifecycle _lifecycle;
        private readonly DynamicClusterReconnectCoordinator _reconnect;
        private TaskCompletionSource _topologyChanged = CreateTopologyChangedSignal();
        private int _initialConnectCoordinatorCount;
        private int _telemetryActiveEndpointCount;
        private int _telemetryReadyEndpointCount;
        private int _telemetryDrainingEndpointCount;

        public DynamicClusterRuntime(
            SharpLinkClient client,
            DynamicClientRuntimeTopologyComposition topology)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            ArgumentNullException.ThrowIfNull(topology);
            _resolver = topology.Resolver;
            _transportFactory = topology.TransportFactory;
            _options = topology.ClusterOptions;
            _current = new DynamicClusterTopologyState(
                topology.LoadBalancingStrategy,
                topology.EndpointSelector);
            _lifecycle = new DynamicClusterRuntimeLifecycle(
                _client,
                _resolver,
                _gate,
                snapshot => ApplySnapshotAsync(snapshot),
                UpdateClientReadiness);
            _reconnect = new DynamicClusterReconnectCoordinator(
                _client,
                _gate,
                _options,
                _current,
                _connections,
                () => _lifecycle.IsStopping,
                ConnectOneAsync,
                (task, name) => _lifecycle.TrackTask(task, name));
        }

        public int ReadyConnectionCount => _current.ReadyConnectionCount;

        public int PendingCallCount => CountConnections(static connection => connection.PendingCalls.Count);

        public int ActiveCallCount => CountConnections(static connection => connection.ActiveCallCount);

        public int ActiveStreamCount => CountConnections(static connection =>
            connection.Session.StreamManager.ActiveStreamCount);

        public void BeginStop() => _lifecycle.BeginStop();

        public ValueTask ConnectAsync(CancellationToken cancellationToken)
            => _lifecycle.ConnectAsync(
                cancellationToken,
                () => ReadyConnectionCount,
                () => _current.Current.Length != 0,
                StartAsync,
                WaitForRecoveryAsync);

        public ClientConnection GetReadyConnection(
            RpcMethodDescriptor? method,
            EndpointRetrySelectionState? retrySelection,
            AttemptOutcomeState? attemptOutcome)
        {
            var snapshot = _current.SelectionSnapshot;
            var endpoints = snapshot.Endpoints;
            if (endpoints.Length == 0)
            {
                SharpLinkTelemetry.RecordSelectionFailure("no_ready_endpoint");
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "No SharpLink endpoint is ready.");
            }

            var excluded = retrySelection?.GetExcludedMask(snapshot, endpoints.Length) ?? 0UL;
            for (var attempt = 0; attempt < endpoints.Length; attempt++)
            {
                int selectedIndex;
                if (_current.HasCustomSelector)
                {
                    try
                    {
                        selectedIndex = _current.SelectEndpoint(snapshot, excluded);
                    }
                    catch (Exception exception)
                    {
                        _client._logger.LogError(exception, "SharpLink endpoint selector failed.");
                        throw new SharpLinkException(
                            SharpLinkErrorCode.FailedPrecondition,
                            "The endpoint selector failed.",
                            exception);
                    }
                }
                else
                {
                    selectedIndex = _current.SelectEndpoint(snapshot, excluded);
                }
                if ((uint)selectedIndex >= (uint)endpoints.Length || (excluded & (1UL << selectedIndex)) != 0)
                {
                    throw new SharpLinkException(
                        SharpLinkErrorCode.FailedPrecondition,
                        "The endpoint selector returned an unavailable candidate index.");
                }
                var candidate = snapshot.Candidates[selectedIndex];
                if (method is not null && attemptOutcome is not null && !attemptOutcome.TryAcquire(candidate))
                {
                    retrySelection?.Exclude(snapshot, selectedIndex);
                    excluded |= 1UL << selectedIndex;
                    continue;
                }
                var endpoint = endpoints[selectedIndex];
                var connection = DynamicClusterTopologyState.SelectConnection(endpoint);
                retrySelection?.Exclude(snapshot, selectedIndex);
                if (connection is not null)
                {
                    if (connection.ActiveCallCount != 0)
                        EnsureExpansion(endpoint);
                    return connection;
                }
                attemptOutcome?.CompleteWithoutPending(
                    PendingCallCompletionReason.ConnectionClosed,
                    CreateConnectionClosedException("The selected dynamic endpoint connection is no longer ready."));
                RetireAdmissionStateIfReleased(endpoint, candidate);
                excluded |= 1UL << selectedIndex;
            }

            SharpLinkTelemetry.RecordSelectionFailure("no_admitted_connection");
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "No SharpLink endpoint connection is ready.");
        }

        public void MarkConnectionDraining(ClientConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            DynamicEndpointState? endpoint;
            var disposeNow = false;
            lock (_gate)
            {
                if (_lifecycle.IsStopping)
                    return;
                if (!_connections.TryMarkDraining(connection, out endpoint, out disposeNow))
                    return;
                PublishReadySnapshotLocked();
                if (disposeNow)
                {
                    _lifecycle.TrackTask(
                        DynamicClusterRuntimeLifecycle.DisposeConnectionAsync(connection),
                        "DynamicClusterForcedRetirementCleanup");
                }
            }
            if (endpoint!.Retiring)
                ScheduleRetiredStateRelease(endpoint);
            else
                _reconnect.EnsureReconnect(endpoint);
            _reconnect.EnsureMinimumReadyEndpoints();
            UpdateClientReadiness();
        }

        public bool TryGetEndpointCandidate(ClientConnection connection, out SharpLinkEndpointCandidate candidate)
        {
            lock (_gate)
            {
                var endpoint = FindEndpointLocked(connection);
                if (endpoint is null)
                {
                    candidate = default;
                    return false;
                }

                candidate = new SharpLinkEndpointCandidate(
                    endpoint.Configuration.Endpoint,
                    endpoint.ReadyConnectionCountProvider,
                    endpoint.ActiveCallCountProvider,
                    endpoint.Generation);
                return true;
            }
        }

        public void HandleConnectionFailure(ClientConnection connection, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(exception);
            DynamicEndpointState? endpoint;
            lock (_gate)
            {
                if (_lifecycle.IsStopping)
                    return;
                endpoint = FindEndpointLocked(connection);
            }
            if (endpoint is not null)
                HandleDisconnected(endpoint, connection, exception);
        }

        public void RetireDrainingConnectionIfIdle(ClientConnection connection)
        {
            if (connection.State != ClientConnectionState.Draining || connection.ActiveCallCount != 0)
                return;
            DynamicEndpointState? endpoint;
            lock (_gate)
            {
                if (_lifecycle.IsStopping)
                    return;
                if (!_connections.TryRetireDrainingIfIdle(connection, out endpoint))
                    return;
                PublishReadySnapshotLocked();
                _lifecycle.TrackTask(
                    DynamicClusterRuntimeLifecycle.DisposeConnectionAsync(connection),
                    "DynamicClusterIdleConnectionCleanup");
            }
            if (endpoint!.Retiring)
                ScheduleRetiredStateRelease(endpoint);
            else
                _reconnect.EnsureReconnect(endpoint);
            _reconnect.EnsureMinimumReadyEndpoints();
            UpdateClientReadiness();
        }

        public ValueTask StopAsync() => _lifecycle.StopAsync(DetachForStopLocked);

        private async Task StartAsync(CancellationToken cancellationToken)
        {
            var resolverSucceeded = false;
            try
            {
                var snapshot = await _resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
                resolverSucceeded = true;
                if (!await ApplySnapshotAsync(snapshot, deferInitialReconciliation: true).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "The endpoint resolver returned an invalid initial topology.");
                }
                _lifecycle.StartResolverWorker(resolveBeforeWatch: false);
                await ConnectCurrentEndpointsAsync(cancellationToken).ConfigureAwait(false);
                UpdateClientReadiness();
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested ||
                _client._shutdownCts.IsCancellationRequested ||
                Volatile.Read(ref _client._stopStarted) != 0)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (!resolverSucceeded)
                    SharpLinkTelemetry.RecordClientResolverFailure();
                _client.TransitionTo(SharpLinkConnectionState.Reconnecting);
                _lifecycle.StartResolverWorker(resolveBeforeWatch: true);
                throw new SharpLinkException(
                    SharpLinkErrorCode.Unavailable,
                    "The endpoint resolver could not provide an initial topology.",
                    exception);
            }
        }

        private async Task WaitForRecoveryAsync()
        {
            while (true)
            {
                if (_lifecycle.IsStopping || _client._shutdownCts.IsCancellationRequested)
                    throw new OperationCanceledException(_client._shutdownCts.Token);
                if (Volatile.Read(ref _client._stopStarted) != 0)
                    throw new OperationCanceledException(_client._shutdownCts.Token);
                if (ReadyConnectionCount != 0 || HasAcceptedEmptyTopology())
                    return;

                _reconnect.EnsureMinimumReadyEndpoints();
                var topologyChanged = CaptureTopologyChangedSignal(out var acceptedEmptyTopology);
                if (acceptedEmptyTopology)
                    return;
                var signal = Volatile.Read(ref _client._readySignal).Task;
                if (ReadyConnectionCount != 0)
                    return;
                await Task.WhenAny(signal, topologyChanged).ConfigureAwait(false);
            }
        }

        private bool HasAcceptedEmptyTopology()
        {
            lock (_gate)
                return _current.HasAcceptedEmptyTopology;
        }

        private Task CaptureTopologyChangedSignal(out bool acceptedEmptyTopology)
        {
            lock (_gate)
            {
                acceptedEmptyTopology = _current.HasAcceptedEmptyTopology;
                return _topologyChanged.Task;
            }
        }

        private static TaskCompletionSource CreateTopologyChangedSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task<bool> ApplySnapshotAsync(
            SharpLinkEndpointSnapshot snapshot,
            bool deferInitialReconciliation = false)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            SharpLinkEndpoint[] endpoints;
            try
            {
                endpoints = SharpClientBuilder.CreateEndpointSnapshot(snapshot.Endpoints, allowEmpty: true);
                if (endpoints.Length > _options.MaxEndpoints)
                    throw new ArgumentException("The endpoint resolver snapshot exceeds MaxEndpoints.", nameof(snapshot));
            }
            catch (Exception exception)
            {
                SharpLinkTelemetry.RecordClientResolverFailure();
                LogClientResolverUpdateFailed(_client._logger, nameof(ApplySnapshotAsync), exception);
                return false;
            }

            Dictionary<string, DynamicEndpointState> previous;
            HashSet<IClientTransportFactory> ownedFactories;
            lock (_gate)
            {
                if (_lifecycle.IsStopping || snapshot.Version <= _current.LastAcceptedVersion)
                    return false;
                previous = _current.SnapshotCurrentById();
                ownedFactories = GetOwnedFactoriesLocked();
            }

            var created = new Dictionary<string, DynamicEndpointState>(StringComparer.Ordinal);
            try
            {
                for (var index = 0; index < endpoints.Length; index++)
                {
                    var endpoint = endpoints[index];
                    if (previous.TryGetValue(endpoint.Id, out var existing) && SameGeneration(existing.Configuration.Endpoint, endpoint))
                        continue;
                    var factory = SharpClientBuilder.CreateRuntimeTransportFactory(endpoint, _transportFactory, _client._runtimeContext);
                    created.Add(
                        endpoint.Id,
                        _current.CreateState(new StaticEndpointConfiguration(endpoint, factory)));
                    if (factory is AnonymousPipeClientTransportFactory)
                    {
                        throw new InvalidOperationException(
                            "Anonymous-pipe handle offers cannot be used by endpoint clusters.");
                    }
                }
            }
            catch (Exception exception)
            {
                lock (_gate)
                    ownedFactories.UnionWith(GetOwnedFactoriesLocked());
                await _lifecycle.DisposeCreatedFactoriesAsync(created.Values, ownedFactories).ConfigureAwait(false);
                SharpLinkTelemetry.RecordClientResolverFailure();
                LogClientResolverUpdateFailed(_client._logger, nameof(ApplySnapshotAsync), exception);
                return false;
            }

            var abandoned = false;
            var rejectedForFactoryOwnership = false;
            var connectionsToDispose = new List<ClientConnection>();
            var statesToRelease = new List<DynamicEndpointState>();
            TaskCompletionSource? topologyChanged = null;
            DynamicEndpointState[] current;
            lock (_gate)
            {
                if (_lifecycle.IsStopping || snapshot.Version <= _current.LastAcceptedVersion)
                {
                    abandoned = true;
                    ownedFactories.UnionWith(GetOwnedFactoriesLocked());
                    current = [];
                }
                else if (!HasUniqueFactoryOwnershipLocked(created.Values))
                {
                    rejectedForFactoryOwnership = true;
                    ownedFactories.UnionWith(GetOwnedFactoriesLocked());
                    current = [];
                }
                else
                {
                    var nextById = new Dictionary<string, DynamicEndpointState>(endpoints.Length, StringComparer.Ordinal);
                    current = new DynamicEndpointState[endpoints.Length];
                    for (var index = 0; index < endpoints.Length; index++)
                    {
                        var endpoint = endpoints[index];
                        DynamicEndpointState state;
                        if (previous.TryGetValue(endpoint.Id, out var existing) && SameGeneration(existing.Configuration.Endpoint, endpoint))
                        {
                            existing.Configuration.ReplaceEndpoint(endpoint);
                            state = existing;
                        }
                        else
                        {
                            state = created[endpoint.Id];
                            _current.AddState(state);
                        }
                        nextById.Add(endpoint.Id, state);
                        current[index] = state;
                    }

                    foreach (var old in _current.Current)
                    {
                        if (!nextById.TryGetValue(old.Configuration.Endpoint.Id, out var replacement) ||
                            !ReferenceEquals(replacement, old))
                        {
                            RetireEndpointLocked(old, connectionsToDispose, statesToRelease);
                        }
                    }

                    _current.CommitCurrent(nextById, current, snapshot.Version);
                    topologyChanged = _topologyChanged;
                    _topologyChanged = CreateTopologyChangedSignal();
                    SharpLinkTelemetry.AddClientActiveEndpoints(current.Length - _telemetryActiveEndpointCount);
                    _telemetryActiveEndpointCount = current.Length;
                    PublishReadySnapshotLocked(force: true);
                    for (var index = 0; index < connectionsToDispose.Count; index++)
                    {
                        _lifecycle.TrackTask(
                            DynamicClusterRuntimeLifecycle.DisposeConnectionAsync(connectionsToDispose[index]),
                            "DynamicClusterTopologyRetirementCleanup");
                    }
                    for (var index = 0; index < statesToRelease.Count; index++)
                        ScheduleRetiredStateReleaseLocked(statesToRelease[index]);
                }
            }

            topologyChanged?.TrySetResult();

            if (abandoned || rejectedForFactoryOwnership)
            {
                await _lifecycle.DisposeCreatedFactoriesAsync(created.Values, ownedFactories).ConfigureAwait(false);
                if (rejectedForFactoryOwnership)
                {
                    SharpLinkTelemetry.RecordClientResolverFailure();
                    LogClientResolverUpdateFailed(
                        _client._logger,
                        nameof(ApplySnapshotAsync),
                        new InvalidOperationException(
                            "A resolver snapshot reused a transport factory owned by another endpoint generation."));
                }
                return false;
            }

            if (!deferInitialReconciliation)
                _reconnect.EnsureMinimumReadyEndpoints();
            SharpLinkTelemetry.RecordClientResolverUpdate();
            return true;
        }

        private void RetireEndpointLocked(
            DynamicEndpointState endpoint,
            List<ClientConnection> connectionsToDispose,
            List<DynamicEndpointState> statesToRelease)
        {
            if (!_connections.BeginEndpointRetirement(endpoint, connectionsToDispose))
                return;
            SharpLinkTelemetry.AddClientDrainingEndpoints(1);
            _telemetryDrainingEndpointCount++;
            if (_connections.CanRelease(endpoint))
                statesToRelease.Add(endpoint);
        }

        private async Task ConnectCurrentEndpointsAsync(CancellationToken cancellationToken)
        {
            DynamicEndpointState[] endpoints;
            lock (_gate)
                endpoints = [.. _current.Current];
            if (endpoints.Length == 0)
                return;

            Interlocked.Increment(ref _initialConnectCoordinatorCount);
            try
            {
                Exception? lastFailure = null;
                var parallelism = Math.Min(Math.Min(_options.MinReadyEndpoints, endpoints.Length), 4);
                var nextEndpoint = 0;
                var remaining = new List<Task<Exception?>>(parallelism);
                while (nextEndpoint < parallelism)
                {
                    var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var endpoint = endpoints[nextEndpoint++];
                    var attempt = TryConnectOneAfterInitialReservationAsync(endpoint, cancellationToken, startGate.Task);
                    TrackInitialDials([endpoint], [attempt]);
                    remaining.Add(attempt);
                    startGate.TrySetResult();
                }

                while (remaining.Count != 0)
                {
                    if (cancellationToken.IsCancellationRequested || _client._shutdownCts.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(
                            cancellationToken.IsCancellationRequested ? cancellationToken : _client._shutdownCts.Token);
                    }
                    if (Volatile.Read(ref _client._stopStarted) != 0)
                        throw new OperationCanceledException(_client._shutdownCts.Token);
                    if (ReadyConnectionCount != 0 || HasAcceptedEmptyTopology())
                    {
                        _reconnect.EnsureMinimumReadyEndpoints();
                        return;
                    }

                    var topologyChanged = CaptureTopologyChangedSignal(out var acceptedEmptyTopology);
                    if (acceptedEmptyTopology)
                    {
                        _reconnect.EnsureMinimumReadyEndpoints();
                        return;
                    }
                    var readySignal = Volatile.Read(ref _client._readySignal).Task;
                    var nextDial = Task.WhenAny(remaining);
                    var completed = await Task.WhenAny(nextDial, readySignal, topologyChanged).ConfigureAwait(false);
                    if (!ReferenceEquals(completed, nextDial))
                        continue;

                    var dial = await nextDial.ConfigureAwait(false);
                    remaining.Remove(dial);
                    lastFailure ??= await dial.ConfigureAwait(false);
                    if (ReadyConnectionCount != 0 || HasAcceptedEmptyTopology())
                    {
                        _reconnect.EnsureMinimumReadyEndpoints();
                        return;
                    }

                    if (nextEndpoint >= endpoints.Length)
                        continue;

                    var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var endpoint = endpoints[nextEndpoint++];
                    var attempt = TryConnectOneAfterInitialReservationAsync(endpoint, cancellationToken, startGate.Task);
                    TrackInitialDials([endpoint], [attempt]);
                    remaining.Add(attempt);
                    startGate.TrySetResult();
                }

                _reconnect.EnsureMinimumReadyEndpoints();
                throw new SharpLinkException(
                    SharpLinkErrorCode.Unavailable,
                    "No dynamic SharpLink endpoint could connect.",
                    lastFailure);
            }
            finally
            {
                if (Interlocked.Decrement(ref _initialConnectCoordinatorCount) == 0 &&
                    !_lifecycle.IsStopping && !_client._shutdownCts.IsCancellationRequested)
                {
                    // A sibling can release its current-generation initial reservation while this
                    // coordinator is active. Reconcile once the coordinator hand-off is complete.
                    _reconnect.EnsureMinimumReadyEndpoints();
                }
            }
        }

        private void TrackInitialDials(DynamicEndpointState[] endpoints, Task<Exception?>[] attempts)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(endpoints.Length, attempts.Length);
            lock (_gate)
            {
                for (var index = 0; index < attempts.Length; index++)
                    endpoints[index].InitialDialReservations++;
                for (var index = 0; index < attempts.Length; index++)
                {
                    _lifecycle.TrackTask(
                        ObserveInitialDialAsync(endpoints[index], attempts[index]),
                        "DynamicClusterInitialDialObserver");
                }
            }
        }

        private async Task ObserveInitialDialAsync(DynamicEndpointState endpoint, Task<Exception?> attempt)
        {
            var shouldReconcile = false;
            try
            {
                _ = await attempt.ConfigureAwait(false);
                shouldReconcile = !_lifecycle.IsStopping;
            }
            catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_gate)
                    endpoint.InitialDialReservations--;
            }
            if (shouldReconcile && Volatile.Read(ref _initialConnectCoordinatorCount) == 0)
                _reconnect.EnsureMinimumReadyEndpoints();
        }

        private async Task<Exception?> TryConnectOneAfterInitialReservationAsync(
            DynamicEndpointState endpoint,
            CancellationToken cancellationToken,
            Task startGate)
        {
            await startGate.ConfigureAwait(false);
            return await TryConnectOneAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }

        private async Task<Exception?> TryConnectOneAsync(DynamicEndpointState endpoint, CancellationToken cancellationToken)
        {
            try
            {
                await ConnectOneAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _client._shutdownCts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private async Task ConnectOneAsync(DynamicEndpointState endpoint, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_lifecycle.IsStopping || _client._shutdownCts.IsCancellationRequested ||
                    endpoint.Retiring || !IsCurrentLocked(endpoint) ||
                    IsRetiringBudgetExceededLocked() ||
                    TotalActiveConnectionsLocked() >= _options.MaxConnections ||
                    _connections.NonRetiringConnectionCount(endpoint) + endpoint.ConnectingCount >= _options.MaxConnectionsPerEndpoint)
                {
                    return;
                }
                endpoint.ConnectingCount++;
            }

            RpcSession? session = null;
            ITransportConnection? transport = null;
            ClientConnection? connection = null;
            Exception? connectFailure = null;
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _client._shutdownCts.Token);
                transport = await endpoint.Configuration.TransportFactory.ConnectAsync(attemptCts.Token).ConfigureAwait(false);
                if (transport is ITransportSecurityInfo securityInfo)
                    LogTlsEstablished(_client._logger, securityInfo.Protocol, securityInfo.CipherSuite);
                session = new RpcSession(
                    transport,
                    new RpcSessionCreationOptions(
                        RpcSessionRole.Client,
                        _client._runtimeContext,
                        _client._rpcSessionFlushOptions));
                transport = null;

                await _client.CompleteHandshakeAsync(session, attemptCts.Token, cancellationToken)
                    .ConfigureAwait(false);

                var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_client._shutdownCts.Token);
                var createdConnection = new ClientConnection(
                    _client,
                    session,
                    sessionCts,
                    _client._protocolOptions.MaxPendingRequestsPerConnection,
                    _client._runtimeContext,
                    endpoint.Configuration.Endpoint.Id,
                    endpoint.Generation);
                connection = createdConnection;
                createdConnection.Session.OnDisconnected += exception => HandleDisconnected(
                    endpoint,
                    createdConnection,
                    exception ?? CreateConnectionClosedException("Transport closed."));

                lock (_gate)
                {
                    if (_lifecycle.IsStopping || endpoint.Retiring || !IsCurrentLocked(endpoint) ||
                        IsRetiringBudgetExceededLocked())
                    {
                        throw CreateConnectionClosedException("Endpoint generation retired while connecting.");
                    }
                    _connections.Add(endpoint, createdConnection);
                    PublishReadySnapshotLocked();
                    session.NotifyConnected();
                    _lifecycle.TrackTask(
                        _client.RunHeartbeatSendLoopAsync(createdConnection, sessionCts.Token),
                        "DynamicClusterHeartbeatSendLoop");
                    _lifecycle.TrackTask(
                        _client.RunProcessRequestLoopAsync(createdConnection, sessionCts.Token),
                        "DynamicClusterProcessRequestLoop");
                }
                session = null;
                connection = null;
                UpdateClientReadiness();
                _reconnect.EnsureMinimumReadyEndpoints();
            }
            catch (Exception exception)
            {
                connectFailure = exception;
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
            if (connectFailure is not null)
            {
                await RethrowAfterFailedConnectionCleanupAsync(connectFailure, transport, connection, session)
                    .ConfigureAwait(false);
            }
        }

        private void HandleDisconnected(DynamicEndpointState endpoint, ClientConnection connection, Exception exception)
        {
            var retired = false;
            lock (_gate)
            {
                if (_lifecycle.IsStopping)
                    return;
                if (!_connections.Remove(endpoint, connection))
                    return;
                retired = endpoint.Retiring;
                PublishReadySnapshotLocked();
                connection.Fail(exception);
                _lifecycle.TrackTask(
                    DynamicClusterRuntimeLifecycle.DisposeConnectionAsync(connection),
                    "DynamicClusterDisconnectedConnectionCleanup");
            }
            if (retired)
                ScheduleRetiredStateRelease(endpoint);
            else if (!_lifecycle.IsStopping)
                _reconnect.EnsureReconnect(endpoint);
            if (!_lifecycle.IsStopping)
                _reconnect.EnsureMinimumReadyEndpoints();
            UpdateClientReadiness();
        }

        private void EnsureExpansion(DynamicEndpointState endpoint)
        {
            lock (_gate)
            {
                if (_lifecycle.IsStopping || endpoint.Retiring || !IsCurrentLocked(endpoint) ||
                    endpoint.ExpansionTask is { IsCompleted: false } ||
                    IsRetiringBudgetExceededLocked() ||
                    TotalActiveConnectionsLocked() >= _options.MaxConnections ||
                    _connections.NonRetiringConnectionCount(endpoint) + endpoint.ConnectingCount >= _options.MaxConnectionsPerEndpoint)
                {
                    return;
                }
                endpoint.ExpansionTask = ExpandAsync(endpoint);
                _lifecycle.TrackTask(endpoint.ExpansionTask, "DynamicClusterExpansion");
            }
        }

        private async Task ExpandAsync(DynamicEndpointState endpoint)
        {
            try
            {
                await ConnectOneAsync(endpoint, _client._shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LogClientConnectionAttemptFailed(_client._logger, nameof(ExpandAsync), exception);
                if (endpoint.ReadyConnections.Length == 0)
                    _reconnect.EnsureReconnect(endpoint);
            }
        }

        private void UpdateClientReadiness()
        {
            if (ReadyConnectionCount != 0)
            {
                _client._readyTimestamp = _client._runtimeContext.TimeProvider.GetTimestamp();
                _client.TransitionTo(SharpLinkConnectionState.Ready);
                return;
            }
            if (!_lifecycle.IsStopping && !_client._shutdownCts.IsCancellationRequested)
                _client.TransitionTo(SharpLinkConnectionState.Reconnecting);
        }

        private void PublishReadySnapshotLocked(bool force = false)
        {
            _connections.PublishReadyConnections(_current.Current);
            var readiness = _current.PublishReadySnapshot(force);
            if (readiness.MembershipChanged)
            {
                SharpLinkTelemetry.AddClientReadyEndpoints(
                    readiness.ReadyEndpoints - _telemetryReadyEndpointCount);
                _telemetryReadyEndpointCount = readiness.ReadyEndpoints;
            }
            _client.PublishReadinessFacts(new ClientReadinessFacts(
                ActiveEndpoints: readiness.ActiveEndpoints,
                ReadyEndpoints: readiness.ReadyEndpoints,
                ReadyConnections: readiness.ReadyConnections,
                TargetReadyEndpoints: Math.Min(
                    _client._maximumReadinessWaitThreshold,
                    readiness.ActiveEndpoints)));
        }

        private DynamicEndpointState? FindEndpointLocked(ClientConnection connection)
            => _connections.FindEndpoint(connection);

        private bool IsCurrentLocked(DynamicEndpointState endpoint)
            => _current.IsCurrent(endpoint);

        private bool IsRetiringBudgetExceededLocked()
            => _connections.IsRetiringBudgetExceeded(_options.MaxRetiringConnections);

        private int TotalActiveConnectionsLocked()
            => _connections.TotalActiveConnections(_current.States);

        private int CountConnections(Func<ClientConnection, int> count)
        {
            lock (_gate)
                return _connections.CountConnections(count);
        }

        private void ScheduleRetiredStateRelease(DynamicEndpointState endpoint)
        {
            lock (_gate)
            {
                if (_lifecycle.IsStopping)
                    return;
                ScheduleRetiredStateReleaseLocked(endpoint);
            }
        }

        private void ScheduleRetiredStateReleaseLocked(DynamicEndpointState endpoint)
            => _lifecycle.TrackTask(
                ReleaseRetiredStateAsync(endpoint),
                "DynamicClusterRetiredTopologyRelease");

        private void RetireAdmissionStateIfReleased(
            DynamicEndpointState endpoint,
            in SharpLinkEndpointCandidate candidate)
        {
            if (_client._endpointAdmissionPolicy is not ISharpLinkEndpointAdmissionLifecycle lifecycle)
                return;

            lock (_gate)
            {
                if (!endpoint.FactoryReleased)
                    return;
            }

            // A selector can retain a published snapshot while topology retirement releases the
            // generation. Its later admission attempt may recreate lazy policy state, so close
            // that stale acquisition after the connection lookup proves the snapshot unusable.
            lifecycle.Retire(candidate);
        }

        private async Task ReleaseRetiredStateAsync(DynamicEndpointState endpoint)
        {
            lock (_gate)
            {
                if (!endpoint.Retiring || endpoint.FactoryReleased || !_connections.CanRelease(endpoint))
                    return;
                endpoint.FactoryReleased = true;
                _connections.ReleaseEndpoint(endpoint);
                _current.RemoveState(endpoint);
                SharpLinkTelemetry.AddClientDrainingEndpoints(-1);
                _telemetryDrainingEndpointCount--;
            }
            if (_client._endpointAdmissionPolicy is ISharpLinkEndpointAdmissionLifecycle lifecycle)
            {
                var candidate = new SharpLinkEndpointCandidate(
                    endpoint.Configuration.Endpoint,
                    endpoint.ReadyConnectionCountProvider,
                    endpoint.ActiveCallCountProvider,
                    endpoint.Generation);
                lifecycle.Retire(candidate);
            }
            await DynamicClusterRuntimeLifecycle.DisposeFactoryQuietlyAsync(endpoint.Configuration.TransportFactory)
                .ConfigureAwait(false);
        }

        private DynamicClusterStopSnapshot DetachForStopLocked()
        {
            var states = _current.States;
            var connections = _connections.DetachAll();
            var stoppedFactories = states
                .Where(static state => !state.FactoryReleased)
                .Select(static state =>
                {
                    state.FactoryReleased = true;
                    return state.Configuration.TransportFactory;
                })
                .ToArray();
            _current.Clear();
            SharpLinkTelemetry.AddClientActiveEndpoints(-_telemetryActiveEndpointCount);
            SharpLinkTelemetry.AddClientReadyEndpoints(-_telemetryReadyEndpointCount);
            SharpLinkTelemetry.AddClientDrainingEndpoints(-_telemetryDrainingEndpointCount);
            _telemetryActiveEndpointCount = 0;
            _telemetryReadyEndpointCount = 0;
            _telemetryDrainingEndpointCount = 0;
            _client.PublishReadinessFacts(new ClientReadinessFacts(
                ActiveEndpoints: 0,
                ReadyEndpoints: 0,
                ReadyConnections: 0,
                TargetReadyEndpoints: 0));
            return new DynamicClusterStopSnapshot(connections, stoppedFactories);
        }

        public ValueTask DisposeResourcesAsync() => _lifecycle.DisposeResourcesAsync();

        private static bool SameGeneration(SharpLinkEndpoint left, SharpLinkEndpoint right)
            => Equals(left.Address, right.Address) && StringComparer.Ordinal.Equals(left.Authority, right.Authority);

        private bool HasUniqueFactoryOwnershipLocked(IEnumerable<DynamicEndpointState> created)
            => _current.HasUniqueFactoryOwnership(created);

        private HashSet<IClientTransportFactory> GetOwnedFactoriesLocked()
            => _current.GetOwnedFactories();
    }
}
