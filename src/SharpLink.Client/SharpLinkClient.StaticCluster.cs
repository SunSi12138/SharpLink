using EndpointState = SharpLink.Client.StaticClientRuntimeEndpointState;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Owns static multi-endpoint transport state without introducing nested SharpLinkClient instances.
    /// The enclosing client continues to own the proxy, interceptor, codec, pending-call and session pipeline.
    /// </summary>
    private sealed class StaticClusterRuntime : IEndpointClusterRuntime
    {
        private readonly SharpLinkClient _client;
        private readonly SharpLinkClusterOptions _options;
        private readonly SharpLinkLoadBalancingStrategy _strategy;
        private readonly ISharpLinkEndpointSelector? _selector;
        private readonly EndpointState[] _endpoints;
        private readonly Lock _gate = new();
        private readonly HashSet<ClientConnection> _retiringConnections = [];
        private EndpointState[] _readyEndpoints = [];
        private EndpointSelectionSnapshot _selectionSnapshot = EndpointSelectionSnapshot.Empty;
        private Task? _connectTask;
        private Task? _stopTask;
        private int _roundRobinCursor;
        private int _leastPendingCursor;
        private int _reconnectCursor;
        private int _initialDialReservations;
        private int _initialConnectCoordinatorCount;
        private int _telemetryReadyEndpointCount;
        private int _stopping;

        private int TargetReadyEndpointCount => Math.Min(_options.MinReadyEndpoints, _endpoints.Length);

        public StaticClusterRuntime(
            SharpLinkClient client,
            StaticClientRuntimeTopologyComposition topology)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            ArgumentNullException.ThrowIfNull(topology);
            _options = topology.ClusterOptions;
            _strategy = topology.LoadBalancingStrategy;
            _selector = topology.EndpointSelector;
            _endpoints = topology.EndpointStates;
            SharpLinkTelemetry.AddClientActiveEndpoints(_endpoints.Length);
        }

        public int ReadyConnectionCount
        {
            get
            {
                var endpoints = Volatile.Read(ref _readyEndpoints);
                var count = 0;
                for (var index = 0; index < endpoints.Length; index++)
                    count += endpoints[index].ReadyConnections.Length;
                return count;
            }
        }

        public int PendingCallCount => CountConnections(static connection => connection.PendingCalls.Count);

        public int ActiveCallCount => CountConnections(static connection => connection.ActiveCallCount);

        public int ActiveStreamCount => CountConnections(static connection =>
            connection.Session.StreamManager.ActiveStreamCount);

        public void BeginStop()
        {
            lock (_gate)
                Volatile.Write(ref _stopping, 1);
        }

        public ValueTask ConnectAsync(CancellationToken cancellationToken)
        {
            Task task;
            lock (_gate)
            {
                if (Volatile.Read(ref _client._stopStarted) != 0 ||
                    Volatile.Read(ref _stopping) != 0 ||
                    _client._shutdownCts.IsCancellationRequested)
                    return ValueTask.FromException(CreateConnectionClosedException("Client has stopped."));
                if (ReadyConnectionCount != 0)
                    return ValueTask.CompletedTask;
                _client.TransitionTo(SharpLinkConnectionState.Connecting);
                // A cluster initialization attempt belongs to the client, not to the first caller.
                // Individual callers still observe their own cancellation through WaitAsync below.
                if (_connectTask is null || _connectTask.IsFaulted || _connectTask.IsCanceled)
                {
                    _connectTask = ConnectInitialAsync(_client._shutdownCts.Token);
                    _client.TrackFrameworkTask(
                        _connectTask,
                        "StaticClusterInitialConnect",
                        TaskObservationMode.ExternallyObserved);
                }
                else if (_connectTask.IsCompleted)
                {
                    _connectTask = WaitForRecoveryAsync();
                    _client.TrackFrameworkTask(
                        _connectTask,
                        "StaticClusterRecoveryWait",
                        TaskObservationMode.ExternallyObserved);
                }
                task = _connectTask;
            }
            return cancellationToken.CanBeCanceled ? new ValueTask(task.WaitAsync(cancellationToken)) : new ValueTask(task);
        }

        public ClientConnection GetReadyConnection(
            RpcMethodDescriptor? method,
            EndpointRetrySelectionState? retrySelection,
            AttemptOutcomeState? attemptOutcome)
        {
            var snapshot = Volatile.Read(ref _selectionSnapshot);
            var endpoints = snapshot.Endpoints;
            if (endpoints.Length == 0)
            {
                SharpLinkTelemetry.RecordSelectionFailure("no_ready_endpoint");
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "No SharpLink endpoint is ready.");
            }

            var excluded = retrySelection?.GetExcludedMask(snapshot, endpoints.Length) ?? 0UL;
            for (var attempt = 0; attempt < endpoints.Length; attempt++)
            {
                var selectedIndex = SelectEndpoint(endpoints, snapshot.Candidates, excluded);
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
                var connection = SelectConnection(endpoints[selectedIndex]);
                retrySelection?.Exclude(snapshot, selectedIndex);
                if (connection is not null)
                {
                    if (connection.ActiveCallCount != 0)
                        EnsureExpansion(endpoints[selectedIndex]);
                    return connection;
                }
                attemptOutcome?.CompleteWithoutPending(
                    PendingCallCompletionReason.ConnectionClosed,
                    CreateConnectionClosedException("The selected static endpoint connection is no longer ready."));
                excluded |= 1UL << selectedIndex;
            }

            SharpLinkTelemetry.RecordSelectionFailure("no_admitted_connection");
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "No SharpLink endpoint connection is ready.");
        }

        public void MarkConnectionDraining(ClientConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            EndpointState? endpoint;
            var retireImmediately = false;
            var forceClose = false;
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0)
                    return;
                endpoint = FindEndpointLocked(connection);
                if (endpoint is null)
                    return;
                connection.MarkDraining();
                if (connection.ActiveCallCount == 0)
                {
                    endpoint.Connections.Remove(connection);
                    PublishReadySnapshotLocked();
                    retireImmediately = true;
                }
                else if (_retiringConnections.Add(connection) &&
                         _retiringConnections.Count > _options.MaxRetiringConnections)
                {
                    _retiringConnections.Remove(connection);
                    endpoint.Connections.Remove(connection);
                    PublishReadySnapshotLocked();
                    forceClose = true;
                }
                else
                {
                    PublishReadySnapshotLocked();
                }
                if (forceClose)
                {
                    connection.Fail(CreateConnectionClosedException(
                        "The static cluster retiring-connection budget was exhausted."));
                    _client.TrackFrameworkTask(
                        DisposeConnectionAsync(connection),
                        "StaticClusterForcedRetirementCleanup");
                }
                else if (retireImmediately)
                {
                    _client.TrackFrameworkTask(
                        DisposeConnectionAsync(connection),
                        "StaticClusterRetiredConnectionCleanup");
                }
            }

            EnsureReconnect(endpoint);
            if (ReadyConnectionCount == 0)
                _client.TransitionTo(SharpLinkConnectionState.Reconnecting);
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
                    generation: 1);
                return true;
            }
        }

        public void HandleConnectionFailure(ClientConnection connection, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(exception);
            EndpointState? endpoint;
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0)
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
            EndpointState? endpoint;
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0)
                    return;
                endpoint = FindEndpointLocked(connection);
                if (endpoint is null)
                    return;
                if (!endpoint.Connections.Remove(connection))
                    return;
                _retiringConnections.Remove(connection);
                PublishReadySnapshotLocked();
                _client.TrackFrameworkTask(
                    DisposeConnectionAsync(connection),
                    "StaticClusterIdleConnectionCleanup");
            }
            EnsureReconnect(endpoint);
        }

        public ValueTask StopAsync()
        {
            lock (_gate)
            {
                _stopTask ??= StopCoreAsync();
                return new ValueTask(_stopTask);
            }
        }

        private async Task ConnectInitialAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _initialConnectCoordinatorCount);
            try
            {
                Exception? lastFailure = null;
                var parallelism = Math.Min(Math.Min(TargetReadyEndpointCount, _endpoints.Length), 4);
                var nextEndpoint = 0;
                var remaining = new List<Task<Exception?>>(parallelism);
                while (nextEndpoint < parallelism)
                {
                    var endpoint = _endpoints[nextEndpoint++];
                    var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var attempt = TryConnectOneAfterInitialReservationAsync(endpoint, cancellationToken, startGate.Task);
                    TrackInitialDials([attempt]);
                    remaining.Add(attempt);
                    startGate.TrySetResult();
                }

                while (remaining.Count != 0)
                {
                    var completed = await Task.WhenAny(remaining).ConfigureAwait(false);
                    remaining.Remove(completed);
                    lastFailure ??= await completed.ConfigureAwait(false);
                    if (ReadyConnectionCount != 0)
                    {
                        EnsureMinimumReadyEndpoints();
                        return;
                    }

                    if (nextEndpoint >= _endpoints.Length)
                        continue;

                    var endpoint = _endpoints[nextEndpoint++];
                    var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var attempt = TryConnectOneAfterInitialReservationAsync(endpoint, cancellationToken, startGate.Task);
                    TrackInitialDials([attempt]);
                    remaining.Add(attempt);
                    startGate.TrySetResult();
                }

                PublishClientReadiness();
                EnsureMinimumReadyEndpoints();
                if (ReadyConnectionCount == 0)
                {
                    _client.TransitionTo(SharpLinkConnectionState.Faulted);
                    throw new SharpLinkException(
                        SharpLinkErrorCode.Unavailable,
                        "No static SharpLink endpoint could connect.",
                        lastFailure);
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _initialConnectCoordinatorCount) == 0 &&
                    Volatile.Read(ref _stopping) == 0 && !_client._shutdownCts.IsCancellationRequested)
                {
                    // A sibling can release its initial reservation while this coordinator is still
                    // active. Its observer intentionally defers reconciliation, so perform the
                    // hand-off reconciliation once the final coordinator exits.
                    EnsureMinimumReadyEndpoints();
                }
            }
        }

        private async Task WaitForRecoveryAsync()
        {
            while (true)
            {
                if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested)
                    throw new OperationCanceledException(_client._shutdownCts.Token);
                if (Volatile.Read(ref _client._stopStarted) != 0)
                    throw new OperationCanceledException(_client._shutdownCts.Token);
                if (ReadyConnectionCount != 0)
                    return;

                EnsureMinimumReadyEndpoints();
                var signal = Volatile.Read(ref _client._readySignal).Task;
                if (ReadyConnectionCount != 0)
                    return;
                await signal.ConfigureAwait(false);
            }
        }

        private void TrackInitialDials(IEnumerable<Task<Exception?>> attempts)
        {
            var tracked = attempts.ToArray();
            lock (_gate)
            {
                _initialDialReservations += tracked.Length;
                foreach (var attempt in tracked)
                {
                    _client.TrackFrameworkTask(
                        ObserveInitialDialAsync(attempt),
                        "StaticClusterInitialDialObserver");
                }
            }
        }

        private async Task ObserveInitialDialAsync(Task<Exception?> attempt)
        {
            var shouldReconcile = false;
            try
            {
                _ = await attempt.ConfigureAwait(false);
                shouldReconcile = Volatile.Read(ref _stopping) == 0;
            }
            catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_gate)
                {
                    _initialDialReservations--;
                }
            }

            if (shouldReconcile && Volatile.Read(ref _initialConnectCoordinatorCount) == 0)
                EnsureMinimumReadyEndpoints();
        }

        private async Task<Exception?> TryConnectOneAfterInitialReservationAsync(
            EndpointState endpoint,
            CancellationToken cancellationToken,
            Task startGate)
        {
            await startGate.ConfigureAwait(false);
            return await TryConnectOneAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }

        private async Task<Exception?> TryConnectOneAsync(
            EndpointState endpoint,
            CancellationToken cancellationToken)
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

        private async Task ConnectOneAsync(EndpointState endpoint, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested)
                    throw CreateConnectionClosedException("Client has stopped.");
                if (TotalConnectionsLocked() >= _options.MaxConnections ||
                    endpoint.NonRetiringConnectionCount + endpoint.ConnectingCount >= _options.MaxConnectionsPerEndpoint)
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
                    endpoint.Configuration.Endpoint.Id);
                connection = createdConnection;
                createdConnection.Session.OnDisconnected += exception => HandleDisconnected(
                    endpoint,
                    createdConnection,
                    exception ?? CreateConnectionClosedException("Transport closed."));

                lock (_gate)
                {
                    if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested)
                        throw CreateConnectionClosedException("Client stopped while connecting.");
                    endpoint.Connections.Add(createdConnection);
                    PublishReadySnapshotLocked();
                    session.NotifyConnected();
                    _client.TrackFrameworkTask(
                        _client.RunHeartbeatSendLoopAsync(createdConnection, sessionCts.Token),
                        "StaticClusterHeartbeatSendLoop");
                    _client.TrackFrameworkTask(
                        _client.RunProcessRequestLoopAsync(createdConnection, sessionCts.Token),
                        "StaticClusterProcessRequestLoop");
                }
                session = null;
                connection = null;
                PublishClientReadiness();
                EnsureMinimumReadyEndpoints();
            }
            catch (Exception exception)
            {
                connectFailure = exception;
            }
            finally
            {
                lock (_gate)
                    endpoint.ConnectingCount--;
            }
            if (connectFailure is not null)
                await RethrowAfterFailedConnectionCleanupAsync(connectFailure, transport, connection, session)
                    .ConfigureAwait(false);
        }

        private void HandleDisconnected(EndpointState endpoint, ClientConnection connection, Exception exception)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0)
                    return;
                if (!endpoint.Connections.Remove(connection))
                    return;
                _retiringConnections.Remove(connection);
                PublishReadySnapshotLocked();
                connection.Fail(exception);
                _client.TrackFrameworkTask(
                    DisposeConnectionAsync(connection),
                    "StaticClusterDisconnectedConnectionCleanup");
            }
            if (Volatile.Read(ref _stopping) == 0)
            {
                _client.TransitionTo(ReadyConnectionCount == 0
                    ? SharpLinkConnectionState.Reconnecting
                    : SharpLinkConnectionState.Ready);
                EnsureReconnect(endpoint);
                EnsureMinimumReadyEndpoints();
            }
        }

        private void EnsureMinimumReadyEndpoints()
        {
            List<EndpointState>? missing = null;
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0)
                    return;
                var readyCount = Volatile.Read(ref _readyEndpoints).Length;
                var availableCapacity = _options.MaxConnections - TotalConnectionsLocked();
                var activeReconnects = _endpoints.Count(static endpoint => endpoint.ReconnectTask is { IsCompleted: false });
                var activeInitialDials = _initialDialReservations;
                var remaining = Math.Min(TargetReadyEndpointCount - readyCount - activeReconnects - activeInitialDials, availableCapacity);
                var start = unchecked((uint)Interlocked.Increment(ref _reconnectCursor));
                for (var offset = 0; remaining > 0 && offset < _endpoints.Length; offset++)
                {
                    var index = (int)((start + (uint)offset) % (uint)_endpoints.Length);
                    var endpoint = _endpoints[index];
                    if (endpoint.ReadyConnections.Length != 0 ||
                        endpoint.NonRetiringConnectionCount + endpoint.ConnectingCount != 0 ||
                        endpoint.ReconnectTask is { IsCompleted: false })
                    {
                        continue;
                    }
                    (missing ??= []).Add(endpoint);
                    remaining--;
                }
            }

            if (missing is null)
                return;
            for (var index = 0; index < missing.Count; index++)
                EnsureReconnect(missing[index]);
        }

        private void EnsureReconnect(EndpointState endpoint)
        {
            lock (_gate)
            {
                var activeReconnects = _endpoints.Count(static candidate => candidate.ReconnectTask is { IsCompleted: false });
                if (endpoint.ReconnectTask is { IsCompleted: false } || !NeedsReconnectLocked(endpoint) ||
                    activeReconnects >= TargetReadyEndpointCount - Volatile.Read(ref _readyEndpoints).Length)
                {
                    return;
                }
                endpoint.ReconnectTask = ReconnectAsync(endpoint);
                _client.TrackFrameworkTask(endpoint.ReconnectTask, "StaticClusterReconnect");
            }
        }

        private void EnsureExpansion(EndpointState endpoint)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0 ||
                    endpoint.ExpansionTask is { IsCompleted: false } ||
                    TotalConnectionsLocked() >= _options.MaxConnections ||
                    endpoint.NonRetiringConnectionCount + endpoint.ConnectingCount >= _options.MaxConnectionsPerEndpoint)
                {
                    return;
                }

                endpoint.ExpansionTask = ExpandAsync(endpoint);
                _client.TrackFrameworkTask(endpoint.ExpansionTask, "StaticClusterExpansion");
            }
        }

        private async Task ExpandAsync(EndpointState endpoint)
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
                    EnsureReconnect(endpoint);
            }
        }

        private async Task ReconnectAsync(EndpointState endpoint)
        {
            int delayMilliseconds;
            lock (_gate)
                delayMilliseconds = endpoint.ReconnectDelayMilliseconds;
            try
            {
                await Task.Delay(
                    _client._reconnectJitter.AddQuarterWindow(delayMilliseconds),
                    _client._runtimeContext.TimeProvider,
                    _client._shutdownCts.Token).ConfigureAwait(false);
                var shouldConnect = false;
                lock (_gate)
                    shouldConnect = NeedsReconnectLocked(endpoint);
                if (shouldConnect)
                {
                    SharpLinkTelemetry.ReconnectAttempt();
                    await ConnectOneAsync(endpoint, _client._shutdownCts.Token).ConfigureAwait(false);
                    lock (_gate)
                        endpoint.ReconnectDelayMilliseconds = endpoint.ReadyConnections.Length != 0 ? 100 : NextReconnectDelay(delayMilliseconds);
                }
            }
            catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogClientConnectionAttemptFailed(_client._logger, nameof(ReconnectAsync), exception);
                lock (_gate)
                    endpoint.ReconnectDelayMilliseconds = NextReconnectDelay(delayMilliseconds);
            }
            finally
            {
                lock (_gate)
                    endpoint.ReconnectTask = null;
            }
            if (Volatile.Read(ref _stopping) == 0 && !_client._shutdownCts.IsCancellationRequested)
                EnsureMinimumReadyEndpoints();
        }

        private static int NextReconnectDelay(int delayMilliseconds) => Math.Min(delayMilliseconds * 2, 5000);

        private void PublishClientReadiness()
        {
            if (ReadyConnectionCount == 0)
                return;
            _client._readyTimestamp = _client._runtimeContext.TimeProvider.GetTimestamp();
            _client.TransitionTo(SharpLinkConnectionState.Ready);
        }

        private void PublishReadySnapshotLocked()
        {
            var ready = new List<EndpointState>(_endpoints.Length);
            var readyConnections = 0;
            for (var index = 0; index < _endpoints.Length; index++)
            {
                var endpoint = _endpoints[index];
                endpoint.PublishReadyConnections();
                var endpointReadyConnections = endpoint.ReadyConnections.Length;
                if (endpointReadyConnections != 0)
                {
                    ready.Add(endpoint);
                    readyConnections += endpointReadyConnections;
                }
            }
            var endpoints = ready.ToArray();
            var existing = Volatile.Read(ref _readyEndpoints);
            if (!HasSameMembership(existing, endpoints))
            {
                var candidates = new SharpLinkEndpointCandidate[endpoints.Length];
                for (var index = 0; index < endpoints.Length; index++)
                {
                    var endpoint = endpoints[index];
                    candidates[index] = new SharpLinkEndpointCandidate(
                        endpoint.Configuration.Endpoint,
                        endpoint.ReadyConnectionCountProvider,
                        endpoint.ActiveCallCountProvider,
                        generation: 1);
                }
                Volatile.Write(ref _readyEndpoints, endpoints);
                Volatile.Write(ref _selectionSnapshot, new EndpointSelectionSnapshot(endpoints, candidates));
                SharpLinkTelemetry.AddClientReadyEndpoints(endpoints.Length - _telemetryReadyEndpointCount);
                _telemetryReadyEndpointCount = endpoints.Length;
            }
            _client.PublishReadinessFacts(new ClientReadinessFacts(
                ActiveEndpoints: _endpoints.Length,
                ReadyEndpoints: endpoints.Length,
                ReadyConnections: readyConnections,
                TargetReadyEndpoints: TargetReadyEndpointCount));
        }

        private static bool HasSameMembership(EndpointState[] left, EndpointState[] right)
        {
            if (left.Length != right.Length)
                return false;
            for (var index = 0; index < left.Length; index++)
                if (!ReferenceEquals(left[index], right[index]))
                    return false;
            return true;
        }

        private int SelectEndpoint(
            EndpointState[] endpoints,
            SharpLinkEndpointCandidate[] candidates,
            ulong excluded)
        {
            var availableCount = 0;
            for (var index = 0; index < endpoints.Length; index++)
                availableCount += (excluded & (1UL << index)) == 0 ? 1 : 0;
            if (availableCount == 0)
                return -1;
            if (availableCount == 1 && _selector is null)
            {
                for (var index = 0; index < endpoints.Length; index++)
                    if ((excluded & (1UL << index)) == 0)
                        return index;
            }
            if (_selector is not null)
            {
                try
                {
                    return _selector.Select(new SharpLinkEndpointSelectionContext(candidates, excluded));
                }
                catch (Exception exception)
                {
                    _client._logger.LogError(exception, "SharpLink endpoint selector failed.");
                    throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "The endpoint selector failed.", exception);
                }
            }
            return _strategy switch
            {
                SharpLinkLoadBalancingStrategy.Random => SelectRandom(endpoints.Length, excluded, availableCount),
                SharpLinkLoadBalancingStrategy.RoundRobin => SelectRoundRobin(endpoints.Length, excluded),
                SharpLinkLoadBalancingStrategy.LeastPending => SelectLeastPending(endpoints, excluded),
                _ => SelectPowerOfTwo(endpoints, excluded, availableCount)
            };
        }

        private int SelectPowerOfTwo(EndpointState[] endpoints, ulong excluded, int availableCount)
        {
            var first = SelectRandom(endpoints.Length, excluded, availableCount);
            var second = SelectRandom(endpoints.Length, excluded | (1UL << first), availableCount - 1);
            if (second < 0)
                return first;
            var firstState = endpoints[first];
            var secondState = endpoints[second];
            return EndpointSelectionKernel.CompareNormalizedLoad(
                firstState.ActiveCallCount,
                firstState.ReadyConnections.Length,
                secondState.ActiveCallCount,
                secondState.ReadyConnections.Length) <= 0
                ? first
                : second;
        }

        private static int SelectRandom(int length, ulong excluded, int availableCount)
        {
            if (availableCount <= 0)
                return -1;
            return EndpointSelectionKernel.SelectRandomIndex(
                length,
                excluded,
                availableCount,
                Random.Shared.Next(availableCount));
        }

        private int SelectRoundRobin(int length, ulong excluded)
        {
            return EndpointSelectionKernel.SelectRoundRobinIndex(ref _roundRobinCursor, length, excluded);
        }

        private int SelectLeastPending(EndpointState[] endpoints, ulong excluded)
        {
            var start = unchecked((uint)Interlocked.Increment(ref _leastPendingCursor));
            var selected = -1;
            for (var offset = 0; offset < endpoints.Length; offset++)
            {
                var index = (int)((start + (uint)offset) % (uint)endpoints.Length);
                if ((excluded & (1UL << index)) != 0)
                    continue;
                if (selected < 0 || endpoints[index].ActiveCallCount < endpoints[selected].ActiveCallCount)
                    selected = index;
            }
            return selected;
        }

        private static ClientConnection? SelectConnection(EndpointState endpoint)
            => EndpointSelectionKernel.SelectConnection(endpoint.ReadyConnections);

        private EndpointState? FindEndpointLocked(ClientConnection connection)
        {
            for (var index = 0; index < _endpoints.Length; index++)
                if (_endpoints[index].Connections.Contains(connection))
                    return _endpoints[index];
            return null;
        }

        private int TotalConnectionsLocked()
        {
            var count = 0;
            for (var index = 0; index < _endpoints.Length; index++)
                count += _endpoints[index].NonRetiringConnectionCount + _endpoints[index].ConnectingCount;
            return count;
        }

        private bool NeedsReconnectLocked(EndpointState endpoint)
            => Volatile.Read(ref _stopping) == 0 && !_client._shutdownCts.IsCancellationRequested &&
               Volatile.Read(ref _readyEndpoints).Length < TargetReadyEndpointCount &&
               TotalConnectionsLocked() < _options.MaxConnections &&
               endpoint.NonRetiringConnectionCount + endpoint.ConnectingCount == 0;

        private int CountConnections(Func<ClientConnection, int> count)
        {
            lock (_gate)
            {
                var result = 0;
                for (var index = 0; index < _endpoints.Length; index++)
                    foreach (var connection in _endpoints[index].Connections)
                        result += count(connection);
                return result;
            }
        }

        private async Task StopCoreAsync()
        {
            Interlocked.Exchange(ref _stopping, 1);
            var cleanupFailures = new List<Exception>();
            ClientConnection[] connections;
            lock (_gate)
            {
                connections = [.. _endpoints.SelectMany(static endpoint => endpoint.Connections)];
                for (var index = 0; index < _endpoints.Length; index++)
                    _endpoints[index].Connections.Clear();
                _retiringConnections.Clear();
                Volatile.Write(ref _readyEndpoints, []);
                Volatile.Write(ref _selectionSnapshot, EndpointSelectionSnapshot.Empty);
                SharpLinkTelemetry.AddClientReadyEndpoints(-_telemetryReadyEndpointCount);
                _telemetryReadyEndpointCount = 0;
                _client.PublishReadinessFacts(new ClientReadinessFacts(
                    ActiveEndpoints: _endpoints.Length,
                    ReadyEndpoints: 0,
                    ReadyConnections: 0,
                    TargetReadyEndpoints: TargetReadyEndpointCount));
            }
            SharpLinkTelemetry.AddClientActiveEndpoints(-_endpoints.Length);
            var stopping = CreateConnectionClosedException("Client is stopping.");
            for (var index = 0; index < connections.Length; index++)
            {
                connections[index].Fail(stopping);
                try { await DisposeConnectionAsync(connections[index]).ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            ThrowCleanupFailures(cleanupFailures);
        }

        public async ValueTask DisposeResourcesAsync()
        {
            var cleanupFailures = new List<Exception>();
            for (var index = 0; index < _endpoints.Length; index++)
            {
                try { await _endpoints[index].Configuration.TransportFactory.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            ThrowCleanupFailures(cleanupFailures);
        }

        private static void ThrowCleanupFailures(List<Exception> failures)
        {
            if (failures.Count == 0)
                return;
            if (failures.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            throw new AggregateException(failures);
        }

        private static async Task DisposeConnectionAsync(ClientConnection connection)
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) { }
        }

        private sealed class EndpointSelectionSnapshot(
            EndpointState[] endpoints,
            SharpLinkEndpointCandidate[] candidates)
        {
            public static readonly EndpointSelectionSnapshot Empty = new([], []);
            public EndpointState[] Endpoints { get; } = endpoints;
            public SharpLinkEndpointCandidate[] Candidates { get; } = candidates;
        }
    }
}
