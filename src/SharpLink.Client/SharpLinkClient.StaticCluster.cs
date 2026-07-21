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
        private readonly HashSet<Task> _initialDialTasks = [];
        private EndpointState[] _readyEndpoints = [];
        private EndpointSelectionSnapshot _selectionSnapshot = EndpointSelectionSnapshot.Empty;
        private Task? _connectTask;
        private Task? _stopTask;
        private int _roundRobinCursor;
        private int _leastPendingCursor;
        private int _stopping;

        private int TargetReadyEndpointCount => Math.Min(_options.MinReadyEndpoints, _endpoints.Length);

        public StaticClusterRuntime(
            SharpLinkClient client,
            StaticEndpointConfiguration[] configurations,
            SharpLinkClusterOptions options,
            SharpLinkLoadBalancingStrategy strategy,
            ISharpLinkEndpointSelector? selector)
        {
            _client = client;
            _options = options;
            _strategy = strategy;
            _selector = selector;
            _endpoints = new EndpointState[configurations.Length];
            for (var index = 0; index < configurations.Length; index++)
                _endpoints[index] = new EndpointState(configurations[index], index);
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
                // A cluster initialization attempt belongs to the client, not to the first caller.
                // Individual callers still observe their own cancellation through WaitAsync below.
                _connectTask ??= ConnectInitialAsync(_client._shutdownCts.Token);
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
            }

            if (forceClose)
            {
                connection.Fail(CreateConnectionClosedException(
                    "The static cluster retiring-connection budget was exhausted."));
                _client.TrackBackgroundTask(DisposeConnectionAsync(connection));
            }
            else if (retireImmediately)
            {
                _client.TrackBackgroundTask(DisposeConnectionAsync(connection));
            }

            EnsureReconnect(endpoint);
            if (ReadyConnectionCount == 0)
                _client.TransitionTo(SharpLinkConnectionState.Reconnecting);
        }

        public void RetireDrainingConnectionIfIdle(ClientConnection connection)
        {
            if (connection.State != ClientConnectionState.Draining || connection.ActiveCallCount != 0)
                return;
            EndpointState? endpoint;
            lock (_gate)
            {
                endpoint = FindEndpointLocked(connection);
                if (endpoint is null)
                    return;
                if (!endpoint.Connections.Remove(connection))
                    return;
                _retiringConnections.Remove(connection);
                PublishReadySnapshotLocked();
            }
            _client.TrackBackgroundTask(DisposeConnectionAsync(connection));
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
            Exception? lastFailure = null;
            var parallelism = Math.Min(Math.Min(_options.MaxConnections, _endpoints.Length), 4);
            for (var start = 0; start < _endpoints.Length && ReadyConnectionCount == 0; start += parallelism)
            {
                var count = Math.Min(parallelism, _endpoints.Length - start);
                var attempts = new Task<Exception?>[count];
                for (var index = 0; index < count; index++)
                {
                    var endpoint = _endpoints[start + index];
                    attempts[index] = TryConnectOneAsync(endpoint, cancellationToken);
                }
                var remaining = new List<Task<Exception?>>(attempts);
                while (remaining.Count != 0)
                {
                    var completed = await Task.WhenAny(remaining).ConfigureAwait(false);
                    remaining.Remove(completed);
                    lastFailure ??= await completed.ConfigureAwait(false);
                    if (ReadyConnectionCount != 0)
                    {
                        TrackInitialDials(remaining);
                        return;
                    }
                }
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

        private void TrackInitialDials(IEnumerable<Task<Exception?>> attempts)
        {
            var tracked = attempts.ToArray();
            lock (_gate)
            {
                foreach (var attempt in tracked)
                    _initialDialTasks.Add(attempt);
            }
            foreach (var attempt in tracked)
                _client.TrackBackgroundTask(ObserveInitialDialAsync(attempt));
        }

        private async Task ObserveInitialDialAsync(Task<Exception?> attempt)
        {
            try
            {
                if (await attempt.ConfigureAwait(false) is not null && Volatile.Read(ref _stopping) == 0)
                    EnsureMinimumReadyEndpoints();
            }
            catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_gate)
                    _initialDialTasks.Remove(attempt);
            }
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
                    endpoint.Configuration.Endpoint.Id);
                connection.Session.OnDisconnected += exception => HandleDisconnected(
                    endpoint,
                    connection,
                    exception ?? CreateConnectionClosedException("Transport closed."));

                lock (_gate)
                {
                    if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested)
                        throw CreateConnectionClosedException("Client stopped while connecting.");
                    endpoint.Connections.Add(connection);
                    PublishReadySnapshotLocked();
                }
                session.NotifyConnected();
                _client.TrackBackgroundTask(_client.RunHeartbeatSendLoopAsync(connection, sessionCts.Token));
                _client.TrackBackgroundTask(_client.RunProcessRequestLoopAsync(connection, sessionCts.Token));
                session = null;
                PublishClientReadiness();
                EnsureMinimumReadyEndpoints();
            }
            finally
            {
                lock (_gate)
                    endpoint.ConnectingCount--;
                if (transport is not null)
                    await transport.DisposeAsync().ConfigureAwait(false);
                if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void HandleDisconnected(EndpointState endpoint, ClientConnection connection, Exception exception)
        {
            lock (_gate)
            {
                if (!endpoint.Connections.Remove(connection))
                    return;
                _retiringConnections.Remove(connection);
                PublishReadySnapshotLocked();
            }
            connection.Fail(exception);
            _client.TrackBackgroundTask(DisposeConnectionAsync(connection));
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
                var remaining = TargetReadyEndpointCount - readyCount;
                for (var index = 0; remaining > 0 && index < _endpoints.Length; index++)
                {
                    var endpoint = _endpoints[index];
                    if (endpoint.ReadyConnections.Length != 0 ||
                        endpoint.NonRetiringConnectionCount + endpoint.ConnectingCount != 0)
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
                if (Volatile.Read(ref _stopping) != 0 || endpoint.ReconnectTask is { IsCompleted: false } ||
                    endpoint.NonRetiringConnectionCount + endpoint.ConnectingCount >= 1)
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
                if (Volatile.Read(ref _stopping) != 0 ||
                    endpoint.ExpansionTask is { IsCompleted: false } ||
                    TotalConnectionsLocked() >= _options.MaxConnections ||
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
            var delayMilliseconds = 100;
            while (Volatile.Read(ref _stopping) == 0 && !_client._shutdownCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), _client._shutdownCts.Token).ConfigureAwait(false);
                    await ConnectOneAsync(endpoint, _client._shutdownCts.Token).ConfigureAwait(false);
                    if (endpoint.ReadyConnections.Length != 0)
                        return;
                }
                catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    LogClientBackgroundLoopUnhandledException(_client._logger, nameof(ReconnectAsync), exception);
                    delayMilliseconds = Math.Min(delayMilliseconds * 2, 5000);
                }
            }
        }

        private void PublishClientReadiness()
        {
            if (ReadyConnectionCount == 0)
                return;
            _client._readyTimestamp = Stopwatch.GetTimestamp();
            _client.TransitionTo(SharpLinkConnectionState.Ready);
            Volatile.Read(ref _client._readySignal).TrySetResult(true);
        }

        private void PublishReadySnapshotLocked()
        {
            var ready = new List<EndpointState>(_endpoints.Length);
            for (var index = 0; index < _endpoints.Length; index++)
            {
                var endpoint = _endpoints[index];
                endpoint.PublishReadyConnections();
                if (endpoint.ReadyConnections.Length != 0)
                    ready.Add(endpoint);
            }
            var endpoints = ready.ToArray();
            var existing = Volatile.Read(ref _readyEndpoints);
            if (HasSameMembership(existing, endpoints))
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
                    generation: 1);
            }
            Volatile.Write(ref _readyEndpoints, endpoints);
            Volatile.Write(ref _selectionSnapshot, new EndpointSelectionSnapshot(endpoints, candidates));
            if (endpoints.Length == 0)
                _client.ResetReadySignal();
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
            if (availableCount == 1)
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
            return StaticEndpointSelection.CompareNormalizedLoad(
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
            return StaticEndpointSelection.SelectRandomIndex(
                length,
                excluded,
                availableCount,
                Random.Shared.Next(availableCount));
        }

        private int SelectRoundRobin(int length, ulong excluded)
        {
            return StaticEndpointSelection.SelectRoundRobinIndex(ref _roundRobinCursor, length, excluded);
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
            }
            var stopping = CreateConnectionClosedException("Client is stopping.");
            for (var index = 0; index < connections.Length; index++)
            {
                connections[index].Fail(stopping);
                try { await DisposeConnectionAsync(connections[index]).ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            await WaitForWorkersAsync(cleanupFailures).ConfigureAwait(false);
            for (var index = 0; index < _endpoints.Length; index++)
            {
                try { await _endpoints[index].Configuration.TransportFactory.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            ThrowCleanupFailures(cleanupFailures);
        }

        private async Task WaitForWorkersAsync(List<Exception> cleanupFailures)
        {
            while (true)
            {
                Task[] workers;
                lock (_gate)
                {
                    var pending = new HashSet<Task>();
                    if (_connectTask is { IsCompleted: false })
                        pending.Add(_connectTask);
                    foreach (var endpoint in _endpoints)
                    {
                        if (endpoint.ReconnectTask is { IsCompleted: false })
                            pending.Add(endpoint.ReconnectTask);
                        if (endpoint.ExpansionTask is { IsCompleted: false })
                            pending.Add(endpoint.ExpansionTask);
                    }
                    foreach (var attempt in _initialDialTasks)
                        if (!attempt.IsCompleted)
                            pending.Add(attempt);
                    workers = [.. pending];
                }

                if (workers.Length == 0)
                    return;

                try { await Task.WhenAll(workers).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested) { }
                catch (Exception exception) { cleanupFailures.Add(exception); }
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

        private static async Task DisposeConnectionAsync(ClientConnection connection)
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) { }
        }

        private sealed class EndpointState
        {
            private readonly Func<int> _readyConnectionCountProvider;
            private readonly Func<int> _activeCallCountProvider;
            private ClientConnection[] _readyConnections = [];

            public EndpointState(StaticEndpointConfiguration configuration, int index)
            {
                Configuration = configuration;
                Index = index;
                _readyConnectionCountProvider = GetReadyConnectionCount;
                _activeCallCountProvider = GetActiveCallCount;
            }

            public StaticEndpointConfiguration Configuration { get; }
            public int Index { get; }
            public HashSet<ClientConnection> Connections { get; } = [];
            public ClientConnection[] ReadyConnections => Volatile.Read(ref _readyConnections);
            public Func<int> ReadyConnectionCountProvider => _readyConnectionCountProvider;
            public Func<int> ActiveCallCountProvider => _activeCallCountProvider;
            public int ConnectingCount { get; set; }
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
