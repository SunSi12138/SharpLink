namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Owns a resolver-backed endpoint topology. It deliberately has no nested client: proxy, codec,
    /// interceptor, pending-call, and session processing continue to belong to the enclosing client.
    /// </summary>
    private sealed class DynamicClusterRuntime : IEndpointClusterRuntime
    {
        private readonly SharpLinkClient _client;
        private readonly ISharpLinkEndpointResolver _resolver;
        private readonly SharpLinkEndpointTransportFactory _transportFactory;
        private readonly SharpLinkClusterOptions _options;
        private readonly SharpLinkLoadBalancingStrategy _strategy;
        private readonly ISharpLinkEndpointSelector? _selector;
        private readonly Lock _gate = new();
        private readonly Dictionary<string, EndpointState> _currentById = new(StringComparer.Ordinal);
        private readonly List<EndpointState> _allStates = [];
        private readonly HashSet<ClientConnection> _retiringConnections = [];
        private readonly Dictionary<EndpointState, HashSet<Task<Exception?>>> _initialDialTasks = [];
        private EndpointState[] _current = [];
        private EndpointState[] _readyEndpoints = [];
        private EndpointSelectionSnapshot _selectionSnapshot = EndpointSelectionSnapshot.Empty;
        private Task? _connectTask;
        private Task? _resolverTask;
        private Task? _stopTask;
        private long _lastAcceptedVersion = -1;
        private long _nextGeneration;
        private int _roundRobinCursor;
        private int _leastPendingCursor;
        private int _reconnectCursor;
        private int _initialConnectCoordinatorCount;
        private int _stopping;
        private int _resolverDisposed;

        public DynamicClusterRuntime(
            SharpLinkClient client,
            ISharpLinkEndpointResolver resolver,
            SharpLinkEndpointTransportFactory transportFactory,
            SharpLinkClusterOptions options,
            SharpLinkLoadBalancingStrategy strategy,
            ISharpLinkEndpointSelector? selector)
        {
            _client = client;
            _resolver = resolver;
            _transportFactory = transportFactory;
            _options = options;
            _strategy = strategy;
            _selector = selector;
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
            ((StreamManager)connection.Session.StreamManager).ActiveStreamCount);

        public ValueTask ConnectAsync(CancellationToken cancellationToken)
        {
            Task task;
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested)
                    return ValueTask.FromException(CreateConnectionClosedException("Client has stopped."));
                if (ReadyConnectionCount != 0)
                    return ValueTask.CompletedTask;
                _client.TransitionTo(SharpLinkConnectionState.Connecting);
                if (_connectTask is null || ((_connectTask.IsFaulted || _connectTask.IsCanceled) && _resolverTask is null))
                    _connectTask = StartAsync(_client._shutdownCts.Token);
                else if (_connectTask.IsCompletedSuccessfully)
                    _connectTask = WaitForRecoveryAsync();
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
                    attemptOutcome?.SetConnection(connection);
                    if (connection.ActiveCallCount != 0)
                        EnsureExpansion(endpoints[selectedIndex]);
                    return connection;
                }
                attemptOutcome?.CompleteWithoutPending(
                    PendingCallCompletionReason.ConnectionClosed,
                    CreateConnectionClosedException("The selected dynamic endpoint connection is no longer ready."));
                excluded |= 1UL << selectedIndex;
            }

            SharpLinkTelemetry.RecordSelectionFailure("no_admitted_connection");
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "No SharpLink endpoint connection is ready.");
        }

        public void MarkConnectionDraining(ClientConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            EndpointState? endpoint;
            var disposeNow = false;
            lock (_gate)
            {
                endpoint = FindEndpointLocked(connection);
                if (endpoint is null)
                    return;
                connection.MarkDraining();
                if (connection.ActiveCallCount == 0)
                {
                    endpoint.Connections.Remove(connection);
                    _retiringConnections.Remove(connection);
                    disposeNow = true;
                }
                else
                {
                    _retiringConnections.Add(connection);
                }
                PublishReadySnapshotLocked();
            }

            if (disposeNow)
                _client.TrackBackgroundTask(DisposeConnectionAsync(connection));
            if (endpoint.Retiring)
                ScheduleRetiredStateRelease(endpoint);
            else
                EnsureReconnect(endpoint);
            EnsureMinimumReadyEndpoints();
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

        public void RetireDrainingConnectionIfIdle(ClientConnection connection)
        {
            if (connection.State != ClientConnectionState.Draining || connection.ActiveCallCount != 0)
                return;
            EndpointState? endpoint;
            lock (_gate)
            {
                endpoint = FindEndpointLocked(connection);
                if (endpoint is null || !endpoint.Connections.Remove(connection))
                    return;
                _retiringConnections.Remove(connection);
                PublishReadySnapshotLocked();
            }
            _client.TrackBackgroundTask(DisposeConnectionAsync(connection));
            if (endpoint.Retiring)
                ScheduleRetiredStateRelease(endpoint);
            else
                EnsureReconnect(endpoint);
            EnsureMinimumReadyEndpoints();
            UpdateClientReadiness();
        }

        public ValueTask StopAsync()
        {
            lock (_gate)
            {
                _stopTask ??= StopCoreAsync();
                return new ValueTask(_stopTask);
            }
        }

        private async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = await _resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
                if (!await ApplySnapshotAsync(snapshot, deferInitialReconciliation: true).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "The endpoint resolver returned an invalid initial topology.");
                }
                StartResolverWorker(resolveBeforeWatch: false);
                await ConnectCurrentEndpointsAsync(cancellationToken).ConfigureAwait(false);
                UpdateClientReadiness();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _client._shutdownCts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _client.TransitionTo(SharpLinkConnectionState.Reconnecting);
                StartResolverWorker(resolveBeforeWatch: true);
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
                if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested)
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

        private void StartResolverWorker(bool resolveBeforeWatch)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0 || _resolverTask is { IsCompleted: false })
                    return;
                _resolverTask = RunResolverWorkerAsync(resolveBeforeWatch);
                _client.TrackBackgroundTask(_resolverTask);
            }
        }

        private async Task RunResolverWorkerAsync(bool resolveBeforeWatch)
        {
            var delayMilliseconds = 100;
            var mustResolve = resolveBeforeWatch;
            while (Volatile.Read(ref _stopping) == 0 && !_client._shutdownCts.IsCancellationRequested)
            {
                if (mustResolve)
                {
                    try
                    {
                        var snapshot = await _resolver.ResolveAsync(_client._shutdownCts.Token).ConfigureAwait(false);
                        if (await ApplySnapshotAsync(snapshot).ConfigureAwait(false))
                            delayMilliseconds = 100;
                        mustResolve = false;
                        UpdateClientReadiness();
                    }
                    catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        LogClientBackgroundLoopUnhandledException(_client._logger, nameof(RunResolverWorkerAsync), exception);
                        await DelayResolverRetryAsync(delayMilliseconds).ConfigureAwait(false);
                        delayMilliseconds = Math.Min(delayMilliseconds * 2, 30_000);
                        continue;
                    }
                }

                try
                {
                    await foreach (var snapshot in _resolver.WatchAsync(_client._shutdownCts.Token)
                                       .WithCancellation(_client._shutdownCts.Token)
                                       .ConfigureAwait(false))
                    {
                        if (Volatile.Read(ref _stopping) != 0)
                            return;
                        if (await ApplySnapshotAsync(snapshot).ConfigureAwait(false))
                            delayMilliseconds = 100;
                        UpdateClientReadiness();
                    }
                    mustResolve = true;
                }
                catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    LogClientBackgroundLoopUnhandledException(_client._logger, nameof(RunResolverWorkerAsync), exception);
                    mustResolve = true;
                }

                await DelayResolverRetryAsync(delayMilliseconds).ConfigureAwait(false);
                delayMilliseconds = Math.Min(delayMilliseconds * 2, 30_000);
            }
        }

        private async Task DelayResolverRetryAsync(int delayMilliseconds)
        {
            var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
            await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds * jitter), _client._shutdownCts.Token)
                .ConfigureAwait(false);
        }

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
                LogClientBackgroundLoopUnhandledException(_client._logger, nameof(ApplySnapshotAsync), exception);
                return false;
            }

            Dictionary<string, EndpointState> previous;
            HashSet<IClientTransportFactory> ownedFactories;
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0 || snapshot.Version <= _lastAcceptedVersion)
                    return false;
                previous = new Dictionary<string, EndpointState>(_currentById, StringComparer.Ordinal);
                ownedFactories = GetOwnedFactoriesLocked();
            }

            var created = new Dictionary<string, EndpointState>(StringComparer.Ordinal);
            try
            {
                for (var index = 0; index < endpoints.Length; index++)
                {
                    var endpoint = endpoints[index];
                    if (previous.TryGetValue(endpoint.Id, out var existing) && SameGeneration(existing.Configuration.Endpoint, endpoint))
                        continue;
                    var factory = SharpClientBuilder.CreateTransportFactory(endpoint, _transportFactory, _client._runtimeContext);
                    created.Add(endpoint.Id, new EndpointState(
                        new StaticEndpointConfiguration(endpoint, factory),
                        Interlocked.Increment(ref _nextGeneration)));
                }
            }
            catch (Exception exception)
            {
                lock (_gate)
                    ownedFactories.UnionWith(GetOwnedFactoriesLocked());
                await DisposeCreatedFactoriesAsync(created.Values, ownedFactories).ConfigureAwait(false);
                LogClientBackgroundLoopUnhandledException(_client._logger, nameof(ApplySnapshotAsync), exception);
                return false;
            }

            var abandoned = false;
            var rejectedForFactoryOwnership = false;
            var connectionsToDispose = new List<ClientConnection>();
            var statesToRelease = new List<EndpointState>();
            EndpointState[] current;
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0 || snapshot.Version <= _lastAcceptedVersion)
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
                    var nextById = new Dictionary<string, EndpointState>(endpoints.Length, StringComparer.Ordinal);
                    current = new EndpointState[endpoints.Length];
                    for (var index = 0; index < endpoints.Length; index++)
                    {
                        var endpoint = endpoints[index];
                        EndpointState state;
                        if (previous.TryGetValue(endpoint.Id, out var existing) && SameGeneration(existing.Configuration.Endpoint, endpoint))
                        {
                            existing.Configuration.ReplaceEndpoint(endpoint);
                            state = existing;
                        }
                        else
                        {
                            state = created[endpoint.Id];
                            _allStates.Add(state);
                        }
                        nextById.Add(endpoint.Id, state);
                        current[index] = state;
                    }

                    foreach (var old in _current)
                    {
                        if (!nextById.TryGetValue(old.Configuration.Endpoint.Id, out var replacement) ||
                            !ReferenceEquals(replacement, old))
                        {
                            RetireEndpointLocked(old, connectionsToDispose, statesToRelease);
                        }
                    }

                    _currentById.Clear();
                    foreach (var pair in nextById)
                        _currentById.Add(pair.Key, pair.Value);
                    _current = current;
                    _lastAcceptedVersion = snapshot.Version;
                    PublishReadySnapshotLocked(force: true);
                }
            }

            if (abandoned || rejectedForFactoryOwnership)
            {
                await DisposeCreatedFactoriesAsync(created.Values, ownedFactories).ConfigureAwait(false);
                if (rejectedForFactoryOwnership)
                {
                    LogClientBackgroundLoopUnhandledException(
                        _client._logger,
                        nameof(ApplySnapshotAsync),
                        new InvalidOperationException(
                            "A resolver snapshot reused a transport factory owned by another endpoint generation."));
                }
                return false;
            }

            for (var index = 0; index < connectionsToDispose.Count; index++)
                _client.TrackBackgroundTask(DisposeConnectionAsync(connectionsToDispose[index]));
            for (var index = 0; index < statesToRelease.Count; index++)
                ScheduleRetiredStateRelease(statesToRelease[index]);
            if (!deferInitialReconciliation)
                EnsureMinimumReadyEndpoints();
            return true;
        }

        private void RetireEndpointLocked(
            EndpointState endpoint,
            List<ClientConnection> connectionsToDispose,
            List<EndpointState> statesToRelease)
        {
            if (endpoint.Retiring)
                return;
            endpoint.Retiring = true;
            var connections = endpoint.Connections.ToArray();
            for (var index = 0; index < connections.Length; index++)
            {
                var connection = connections[index];
                connection.MarkDraining();
                if (connection.ActiveCallCount == 0)
                {
                    endpoint.Connections.Remove(connection);
                    _retiringConnections.Remove(connection);
                    connectionsToDispose.Add(connection);
                }
                else
                {
                    _retiringConnections.Add(connection);
                }
            }
            if (endpoint.Connections.Count == 0 && endpoint.ConnectingCount == 0)
                statesToRelease.Add(endpoint);
        }

        private async Task ConnectCurrentEndpointsAsync(CancellationToken cancellationToken)
        {
            EndpointState[] endpoints;
            lock (_gate)
                endpoints = [.. _current];
            if (endpoints.Length == 0)
                return;

            Interlocked.Increment(ref _initialConnectCoordinatorCount);
            try
            {
                Exception? lastFailure = null;
                var parallelism = Math.Min(Math.Min(_options.MinReadyEndpoints, endpoints.Length), 4);
                for (var start = 0; start < endpoints.Length; start += parallelism)
                {
                    var count = Math.Min(parallelism, endpoints.Length - start);
                    var tasks = new Task<Exception?>[count];
                    var batchEndpoints = new EndpointState[count];
                    var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    for (var index = 0; index < count; index++)
                    {
                        var endpoint = endpoints[start + index];
                        batchEndpoints[index] = endpoint;
                        tasks[index] = TryConnectOneAfterInitialReservationAsync(
                            endpoint, cancellationToken, startGate.Task);
                    }
                    TrackInitialDials(batchEndpoints, tasks);
                    startGate.TrySetResult();

                    var remaining = new List<Task<Exception?>>(tasks);
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
                    }
                }

                EnsureMinimumReadyEndpoints();
                throw new SharpLinkException(
                    SharpLinkErrorCode.Unavailable,
                    "No dynamic SharpLink endpoint could connect.",
                    lastFailure);
            }
            finally
            {
                if (Interlocked.Decrement(ref _initialConnectCoordinatorCount) == 0 &&
                    Volatile.Read(ref _stopping) == 0 && !_client._shutdownCts.IsCancellationRequested)
                {
                    // A sibling can release its current-generation initial reservation while this
                    // coordinator is active. Reconcile once the coordinator hand-off is complete.
                    EnsureMinimumReadyEndpoints();
                }
            }
        }

        private void TrackInitialDials(EndpointState[] endpoints, Task<Exception?>[] attempts)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(endpoints.Length, attempts.Length);
            lock (_gate)
            {
                for (var index = 0; index < attempts.Length; index++)
                {
                    var endpoint = endpoints[index];
                    if (!_initialDialTasks.TryGetValue(endpoint, out var endpointAttempts))
                    {
                        endpointAttempts = [];
                        _initialDialTasks.Add(endpoint, endpointAttempts);
                    }
                    endpointAttempts.Add(attempts[index]);
                    endpoint.InitialDialReservations++;
                }
            }
            for (var index = 0; index < attempts.Length; index++)
                _client.TrackBackgroundTask(ObserveInitialDialAsync(endpoints[index], attempts[index]));
        }

        private async Task ObserveInitialDialAsync(EndpointState endpoint, Task<Exception?> attempt)
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
                    if (_initialDialTasks.TryGetValue(endpoint, out var endpointAttempts))
                    {
                        endpointAttempts.Remove(attempt);
                        endpoint.InitialDialReservations--;
                        if (endpointAttempts.Count == 0)
                            _initialDialTasks.Remove(endpoint);
                    }
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

        private async Task<Exception?> TryConnectOneAsync(EndpointState endpoint, CancellationToken cancellationToken)
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
                if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested ||
                    endpoint.Retiring || !IsCurrentLocked(endpoint) ||
                    IsRetiringBudgetExceededLocked() ||
                    TotalActiveConnectionsLocked() >= _options.MaxConnections ||
                    endpoint.NonRetiringConnectionCount + endpoint.ConnectingCount >= _options.MaxConnectionsPerEndpoint)
                {
                    return;
                }
                endpoint.ConnectingCount++;
            }

            RpcSession? session = null;
            ITransportConnection? transport = null;
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _client._shutdownCts.Token);
                transport = await endpoint.Configuration.TransportFactory.ConnectAsync(attemptCts.Token).ConfigureAwait(false);
                if (transport is ITransportSecurityInfo securityInfo)
                    LogTlsEstablished(_client._logger, securityInfo.Protocol, securityInfo.CipherSuite);
                session = new RpcSession(transport, _client._rpcSessionFlushOptions);
                transport = null;
                session.SetTelemetrySide("client");
                session.BindRuntimeContext(_client._runtimeContext);

                using var handshakeTimeout = new CancellationTokenSource(_client._protocolOptions.HandshakeTimeout);
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(attemptCts.Token, handshakeTimeout.Token);
                var handshakeException = await _client.ProcessHandshakeAsync(session, handshakeCts.Token).ConfigureAwait(false);
                if (handshakeException is not null)
                    throw handshakeException;

                var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_client._shutdownCts.Token);
                var connection = new ClientConnection(
                    _client,
                    session,
                    sessionCts,
                    _client._protocolOptions.MaxPendingRequestsPerConnection,
                    _client._runtimeContext.Codecs,
                    endpoint.Configuration.Endpoint.Id,
                    endpoint.Generation);
                connection.Session.OnDisconnected += exception => HandleDisconnected(
                    endpoint,
                    connection,
                    exception ?? CreateConnectionClosedException("Transport closed."));

                lock (_gate)
                {
                    if (Volatile.Read(ref _stopping) != 0 || endpoint.Retiring || !IsCurrentLocked(endpoint) ||
                        IsRetiringBudgetExceededLocked())
                        throw CreateConnectionClosedException("Endpoint generation retired while connecting.");
                    endpoint.Connections.Add(connection);
                    PublishReadySnapshotLocked();
                }
                session.NotifyConnected();
                _client.TrackBackgroundTask(_client.RunHeartbeatSendLoopAsync(connection, sessionCts.Token));
                _client.TrackBackgroundTask(_client.RunProcessRequestLoopAsync(connection, sessionCts.Token));
                session = null;
                UpdateClientReadiness();
                EnsureMinimumReadyEndpoints();
            }
            finally
            {
                var release = false;
                lock (_gate)
                {
                    endpoint.ConnectingCount--;
                    release = endpoint.Retiring && endpoint.Connections.Count == 0 && endpoint.ConnectingCount == 0;
                }
                if (release)
                    ScheduleRetiredStateRelease(endpoint);
                if (transport is not null)
                    await transport.DisposeAsync().ConfigureAwait(false);
                if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void HandleDisconnected(EndpointState endpoint, ClientConnection connection, Exception exception)
        {
            var retired = false;
            lock (_gate)
            {
                if (!endpoint.Connections.Remove(connection))
                    return;
                _retiringConnections.Remove(connection);
                retired = endpoint.Retiring;
                PublishReadySnapshotLocked();
            }
            connection.Fail(exception);
            _client.TrackBackgroundTask(DisposeConnectionAsync(connection));
            if (retired)
                ScheduleRetiredStateRelease(endpoint);
            else if (Volatile.Read(ref _stopping) == 0)
            {
                EnsureReconnect(endpoint);
            }
            if (Volatile.Read(ref _stopping) == 0)
                EnsureMinimumReadyEndpoints();
            UpdateClientReadiness();
        }

        private void EnsureMinimumReadyEndpoints()
        {
            List<EndpointState>? missing = null;
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0)
                    return;
                var target = Math.Min(_options.MinReadyEndpoints, _current.Length);
                var availableCapacity = _options.MaxConnections - TotalActiveConnectionsLocked();
                var activeReconnects = _current.Count(static endpoint => endpoint.ReconnectTask is { IsCompleted: false });
                var activeInitialDials = CountActiveCurrentInitialDialsLocked();
                var remaining = Math.Min(target - Volatile.Read(ref _readyEndpoints).Length - activeReconnects - activeInitialDials, availableCapacity);
                var start = unchecked((uint)Interlocked.Increment(ref _reconnectCursor));
                for (var offset = 0; remaining > 0 && offset < _current.Length; offset++)
                {
                    var index = (int)((start + (uint)offset) % (uint)_current.Length);
                    var endpoint = _current[index];
                    if (endpoint.ReadyConnections.Length != 0 ||
                        endpoint.NonRetiringConnectionCount + endpoint.ConnectingCount != 0 ||
                        endpoint.ReconnectTask is { IsCompleted: false })
                        continue;
                    (missing ??= []).Add(endpoint);
                    remaining--;
                }
            }

            if (missing is not null)
                for (var index = 0; index < missing.Count; index++)
                    EnsureReconnect(missing[index]);
        }

        private int CountActiveCurrentInitialDialsLocked()
        {
            var count = 0;
            for (var index = 0; index < _current.Length; index++)
                count += _current[index].InitialDialReservations;
            return count;
        }

        private void EnsureReconnect(EndpointState endpoint)
        {
            lock (_gate)
            {
                var target = Math.Min(_options.MinReadyEndpoints, _current.Length);
                var activeReconnects = _current.Count(static candidate => candidate.ReconnectTask is { IsCompleted: false });
                if (endpoint.ReconnectTask is { IsCompleted: false } || !NeedsReconnectLocked(endpoint) ||
                    activeReconnects >= target - Volatile.Read(ref _readyEndpoints).Length)
                {
                    return;
                }
                endpoint.ReconnectTask = ReconnectAsync(endpoint);
                _client.TrackBackgroundTask(endpoint.ReconnectTask);
            }
        }

        private void EnsureExpansion(EndpointState endpoint)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0 || endpoint.Retiring || !IsCurrentLocked(endpoint) ||
                    endpoint.ExpansionTask is { IsCompleted: false } ||
                    IsRetiringBudgetExceededLocked() ||
                    TotalActiveConnectionsLocked() >= _options.MaxConnections ||
                    endpoint.NonRetiringConnectionCount + endpoint.ConnectingCount >= _options.MaxConnectionsPerEndpoint)
                {
                    return;
                }
                endpoint.ExpansionTask = ExpandAsync(endpoint);
                _client.TrackBackgroundTask(endpoint.ExpansionTask);
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
                LogClientBackgroundLoopUnhandledException(_client._logger, nameof(ExpandAsync), exception);
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
                var jitterMilliseconds = Random.Shared.Next(delayMilliseconds / 4 + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds + jitterMilliseconds), _client._shutdownCts.Token).ConfigureAwait(false);
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
                LogClientBackgroundLoopUnhandledException(_client._logger, nameof(ReconnectAsync), exception);
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

        private void UpdateClientReadiness()
        {
            if (ReadyConnectionCount != 0)
            {
                _client._readyTimestamp = Stopwatch.GetTimestamp();
                _client.TransitionTo(SharpLinkConnectionState.Ready);
                Volatile.Read(ref _client._readySignal).TrySetResult(true);
                return;
            }
            if (Volatile.Read(ref _stopping) == 0 && !_client._shutdownCts.IsCancellationRequested)
            {
                _client.ResetReadySignal();
                _client.TransitionTo(SharpLinkConnectionState.Reconnecting);
            }
        }

        private void PublishReadySnapshotLocked(bool force = false)
        {
            var ready = new List<EndpointState>(_current.Length);
            for (var index = 0; index < _current.Length; index++)
            {
                var endpoint = _current[index];
                endpoint.PublishReadyConnections();
                if (endpoint.ReadyConnections.Length != 0)
                    ready.Add(endpoint);
            }
            var endpoints = ready.ToArray();
            var existing = Volatile.Read(ref _readyEndpoints);
            if (!force && HasSameMembership(existing, endpoints))
            {
                if (endpoints.Length == 0)
                    _client.ResetReadySignal();
                return;
            }
            var candidates = new SharpLinkEndpointCandidate[endpoints.Length];
            for (var index = 0; index < endpoints.Length; index++)
            {
                var endpoint = endpoints[index];
                candidates[index] = new SharpLinkEndpointCandidate(
                    endpoint.Configuration.Endpoint,
                    endpoint.ReadyConnectionCountProvider,
                    endpoint.ActiveCallCountProvider,
                    endpoint.Generation);
            }
            Volatile.Write(ref _readyEndpoints, endpoints);
            Volatile.Write(ref _selectionSnapshot, new EndpointSelectionSnapshot(endpoints, candidates));
            if (endpoints.Length == 0)
                _client.ResetReadySignal();
        }

        private int SelectEndpoint(EndpointState[] endpoints, SharpLinkEndpointCandidate[] candidates, ulong excluded)
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
                SharpLinkLoadBalancingStrategy.RoundRobin => StaticEndpointSelection.SelectRoundRobinIndex(ref _roundRobinCursor, endpoints.Length, excluded),
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
            return StaticEndpointSelection.CompareNormalizedLoad(
                firstState.ActiveCallCount, firstState.ReadyConnections.Length,
                secondState.ActiveCallCount, secondState.ReadyConnections.Length) <= 0 ? first : second;
        }

        private static int SelectRandom(int length, ulong excluded, int availableCount)
            => availableCount <= 0 ? -1 : StaticEndpointSelection.SelectRandomIndex(
                length, excluded, availableCount, Random.Shared.Next(availableCount));

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
        {
            var connections = endpoint.ReadyConnections;
            if (connections.Length == 0)
                return null;
            if (connections.Length == 1)
                return connections[0].CanAcceptCalls ? connections[0] : null;
            var first = Random.Shared.Next(connections.Length);
            var second = Random.Shared.Next(connections.Length - 1);
            if (second >= first)
                second++;
            var selected = SelectLeastLoaded(connections, first, second);
            return selected.CanAcceptCalls ? selected : null;
        }

        private EndpointState? FindEndpointLocked(ClientConnection connection)
        {
            for (var index = 0; index < _allStates.Count; index++)
                if (_allStates[index].Connections.Contains(connection))
                    return _allStates[index];
            return null;
        }

        private bool IsCurrentLocked(EndpointState endpoint)
            => _currentById.TryGetValue(endpoint.Configuration.Endpoint.Id, out var current) && ReferenceEquals(current, endpoint);

        private bool NeedsReconnectLocked(EndpointState endpoint)
            => Volatile.Read(ref _stopping) == 0 && !_client._shutdownCts.IsCancellationRequested &&
               !endpoint.Retiring && IsCurrentLocked(endpoint) &&
               !IsRetiringBudgetExceededLocked() &&
               Volatile.Read(ref _readyEndpoints).Length < Math.Min(_options.MinReadyEndpoints, _current.Length) &&
               TotalActiveConnectionsLocked() < _options.MaxConnections &&
               endpoint.NonRetiringConnectionCount + endpoint.ConnectingCount == 0;

        private bool IsRetiringBudgetExceededLocked()
            => _retiringConnections.Count > _options.MaxRetiringConnections;

        private int TotalActiveConnectionsLocked()
        {
            var count = 0;
            for (var index = 0; index < _current.Length; index++)
                count += _current[index].NonRetiringConnectionCount + _current[index].ConnectingCount;
            return count;
        }

        private int CountConnections(Func<ClientConnection, int> count)
        {
            lock (_gate)
            {
                var result = 0;
                for (var index = 0; index < _allStates.Count; index++)
                    foreach (var connection in _allStates[index].Connections)
                        result += count(connection);
                return result;
            }
        }

        private void ScheduleRetiredStateRelease(EndpointState endpoint)
            => _client.TrackBackgroundTask(ReleaseRetiredStateAsync(endpoint));

        private async Task ReleaseRetiredStateAsync(EndpointState endpoint)
        {
            lock (_gate)
            {
                if (!endpoint.Retiring || endpoint.FactoryReleased || endpoint.Connections.Count != 0 || endpoint.ConnectingCount != 0)
                    return;
                endpoint.FactoryReleased = true;
                _allStates.Remove(endpoint);
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
            await DisposeFactoryQuietlyAsync(endpoint.Configuration.TransportFactory).ConfigureAwait(false);
        }

        private async Task StopCoreAsync()
        {
            Interlocked.Exchange(ref _stopping, 1);
            var cleanupFailures = new List<Exception>();
            ClientConnection[] connections;
            Task[] workers;
            Task? initialConnectTask;
            IClientTransportFactory[] factories;
            lock (_gate)
            {
                initialConnectTask = _connectTask;
                connections = [.. _allStates.SelectMany(static state => state.Connections)];
                workers = [.. _allStates
                    .SelectMany(static state => new[] { state.ReconnectTask, state.ExpansionTask })
                    .Concat(_initialDialTasks.Values.SelectMany(static attempts => attempts))
                    .Append(initialConnectTask)
                    .Append(_resolverTask)
                    .Where(static task => task is not null)!];
                factories = [.. _allStates
                    .Where(static state => !state.FactoryReleased)
                    .Select(static state =>
                    {
                        state.FactoryReleased = true;
                        return state.Configuration.TransportFactory;
                    })];
                for (var index = 0; index < _allStates.Count; index++)
                    _allStates[index].Connections.Clear();
                _allStates.Clear();
                _currentById.Clear();
                _current = [];
                _retiringConnections.Clear();
                Volatile.Write(ref _readyEndpoints, []);
                Volatile.Write(ref _selectionSnapshot, EndpointSelectionSnapshot.Empty);
            }

            if (Interlocked.Exchange(ref _resolverDisposed, 1) == 0)
            {
                try { await _resolver.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }

            var stopping = CreateConnectionClosedException("Client is stopping.");
            for (var index = 0; index < connections.Length; index++)
            {
                connections[index].Fail(stopping);
                try { await DisposeConnectionAsync(connections[index]).ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            for (var index = 0; index < workers.Length; index++)
            {
                var worker = workers[index];
                try { await worker.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested) { }
                catch (Exception) when (ReferenceEquals(worker, initialConnectTask)) { }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            await WaitForInitialDialsAsync(cleanupFailures).ConfigureAwait(false);
            for (var index = 0; index < factories.Length; index++)
            {
                try { await DisposeFactoryQuietlyAsync(factories[index]).ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            ThrowCleanupFailures(cleanupFailures);
        }

        private async Task WaitForInitialDialsAsync(List<Exception> cleanupFailures)
        {
            while (true)
            {
                Task[] pending;
                lock (_gate)
                    pending = [.. _initialDialTasks.Values
                        .SelectMany(static attempts => attempts)
                        .Where(static task => !task.IsCompleted)];
                if (pending.Length == 0)
                    return;
                for (var index = 0; index < pending.Length; index++)
                {
                    try { await pending[index].ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested) { }
                    catch (Exception exception) { cleanupFailures.Add(exception); }
                }
            }
        }

        private static void ThrowCleanupFailures(List<Exception> failures)
        {
            if (failures.Count == 0)
                return;
            if (failures.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            throw new AggregateException(failures);
        }

        private static bool SameGeneration(SharpLinkEndpoint left, SharpLinkEndpoint right)
            => Equals(left.Address, right.Address) && StringComparer.Ordinal.Equals(left.Authority, right.Authority);

        private bool HasUniqueFactoryOwnershipLocked(IEnumerable<EndpointState> created)
        {
            var factories = GetOwnedFactoriesLocked();
            foreach (var state in created)
            {
                if (!factories.Add(state.Configuration.TransportFactory))
                    return false;
            }
            return true;
        }

        private HashSet<IClientTransportFactory> GetOwnedFactoriesLocked()
        {
            var factories = new HashSet<IClientTransportFactory>(ReferenceEqualityComparer.Instance);
            for (var index = 0; index < _allStates.Count; index++)
                factories.Add(_allStates[index].Configuration.TransportFactory);
            return factories;
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

        private static async Task DisposeConnectionAsync(ClientConnection connection)
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) { }
        }

        private static async Task DisposeFactoryQuietlyAsync(IClientTransportFactory factory)
        {
            try { await factory.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) { }
        }

        private async Task DisposeCreatedFactoriesAsync(
            IEnumerable<EndpointState> states,
            ISet<IClientTransportFactory>? preservedFactories = null)
        {
            var factories = new HashSet<IClientTransportFactory>(ReferenceEqualityComparer.Instance);
            foreach (var state in states)
            {
                var factory = state.Configuration.TransportFactory;
                if (factories.Add(factory) && (preservedFactories is null || !preservedFactories.Contains(factory)))
                {
                    try { await DisposeFactoryQuietlyAsync(factory).ConfigureAwait(false); }
                    catch (Exception exception)
                    {
                        LogClientBackgroundLoopUnhandledException(
                            _client._logger,
                            nameof(DisposeCreatedFactoriesAsync),
                            exception);
                    }
                }
            }
        }

        private sealed class EndpointState
        {
            private readonly Func<int> _readyConnectionCountProvider;
            private readonly Func<int> _activeCallCountProvider;
            private ClientConnection[] _readyConnections = [];

            public EndpointState(StaticEndpointConfiguration configuration, long generation)
            {
                Configuration = configuration;
                Generation = generation;
                _readyConnectionCountProvider = GetReadyConnectionCount;
                _activeCallCountProvider = GetActiveCallCount;
            }

            public StaticEndpointConfiguration Configuration { get; }
            public long Generation { get; }
            public HashSet<ClientConnection> Connections { get; } = [];
            public ClientConnection[] ReadyConnections => Volatile.Read(ref _readyConnections);
            public Func<int> ReadyConnectionCountProvider => _readyConnectionCountProvider;
            public Func<int> ActiveCallCountProvider => _activeCallCountProvider;
            public int ConnectingCount { get; set; }
            public int InitialDialReservations { get; set; }
            public int ReconnectDelayMilliseconds { get; set; } = 100;
            public bool Retiring { get; set; }
            public bool FactoryReleased { get; set; }
            public Task? ReconnectTask { get; set; }
            public Task? ExpansionTask { get; set; }
            public int NonRetiringConnectionCount
            {
                get
                {
                    var count = 0;
                    foreach (var connection in Connections)
                        if (connection.State == ClientConnectionState.Ready)
                            count++;
                    return count;
                }
            }

            public int ActiveCallCount => GetActiveCallCount();

            private int GetReadyConnectionCount() => ReadyConnections.Length;

            private int GetActiveCallCount()
            {
                var connections = ReadyConnections;
                var count = 0;
                for (var index = 0; index < connections.Length; index++)
                    count += connections[index].ActiveCallCount;
                return count;
            }

            public void PublishReadyConnections()
            {
                var ready = new List<ClientConnection>(Connections.Count);
                foreach (var connection in Connections)
                    if (connection.CanAcceptCalls)
                        ready.Add(connection);
                Volatile.Write(ref _readyConnections, ready.ToArray());
            }
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
