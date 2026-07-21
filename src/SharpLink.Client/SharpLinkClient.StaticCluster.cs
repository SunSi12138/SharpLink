namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Owns static multi-endpoint transport state without introducing nested SharpLinkClient instances.
    /// The enclosing client continues to own the proxy, interceptor, codec, pending-call and session pipeline.
    /// </summary>
    private sealed class StaticClusterRuntime
    {
        private readonly SharpLinkClient _client;
        private readonly SharpLinkClusterOptions _options;
        private readonly SharpLinkLoadBalancingStrategy _strategy;
        private readonly ISharpLinkEndpointSelector? _selector;
        private readonly EndpointState[] _endpoints;
        private readonly Lock _gate = new();
        private EndpointState[] _readyEndpoints = [];
        private SharpLinkEndpointCandidate[] _selectionCandidates = [];
        private Task? _connectTask;
        private Task? _stopTask;
        private int _roundRobinCursor;
        private int _leastPendingCursor;
        private int _stopping;

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

        public ValueTask ConnectAsync(CancellationToken cancellationToken)
        {
            Task task;
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested)
                    return ValueTask.FromException(CreateConnectionClosedException("Client has stopped."));
                if (ReadyConnectionCount != 0)
                    return ValueTask.CompletedTask;
                _connectTask ??= ConnectInitialAsync(cancellationToken);
                task = _connectTask;
            }
            return cancellationToken.CanBeCanceled ? new ValueTask(task.WaitAsync(cancellationToken)) : new ValueTask(task);
        }

        public ClientConnection GetReadyConnection()
        {
            var endpoints = Volatile.Read(ref _readyEndpoints);
            if (endpoints.Length == 0)
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "No SharpLink endpoint is ready.");

            var excluded = 0UL;
            for (var attempt = 0; attempt < endpoints.Length; attempt++)
            {
                var selectedIndex = SelectEndpoint(endpoints, excluded);
                if ((uint)selectedIndex >= (uint)endpoints.Length || (excluded & (1UL << selectedIndex)) != 0)
                {
                    throw new SharpLinkException(
                        SharpLinkErrorCode.FailedPrecondition,
                        "The endpoint selector returned an unavailable candidate index.");
                }
                var connection = SelectConnection(endpoints[selectedIndex]);
                if (connection is not null)
                    return connection;
                excluded |= 1UL << selectedIndex;
            }

            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "No SharpLink endpoint connection is ready.");
        }

        public void MarkConnectionDraining(ClientConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            var endpoint = FindEndpoint(connection);
            if (endpoint is null)
                return;
            connection.MarkDraining();
            lock (_gate)
                PublishReadySnapshotLocked();
            RetireDrainingConnectionIfIdle(connection);
            EnsureReconnect(endpoint);
        }

        public void RetireDrainingConnectionIfIdle(ClientConnection connection)
        {
            if (connection.State != ClientConnectionState.Draining || connection.ActiveCallCount != 0)
                return;
            var endpoint = FindEndpoint(connection);
            if (endpoint is null)
                return;
            lock (_gate)
            {
                if (!endpoint.Connections.Remove(connection))
                    return;
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
            var maxInitial = Math.Min(_options.MaxConnections, _endpoints.Length);
            for (var index = 0; index < maxInitial; index++)
            {
                try
                {
                    await ConnectOneAsync(_endpoints[index], cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lastFailure = exception;
                }
            }

            PublishClientReadiness();
            for (var index = 0; index < _endpoints.Length; index++)
                EnsureReconnect(_endpoints[index]);
            if (ReadyConnectionCount == 0)
            {
                _client.TransitionTo(SharpLinkConnectionState.Faulted);
                throw lastFailure ?? new SharpLinkException(SharpLinkErrorCode.Unavailable, "No endpoint could connect.");
            }
        }

        private async Task ConnectOneAsync(EndpointState endpoint, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0 || _client._shutdownCts.IsCancellationRequested)
                    throw CreateConnectionClosedException("Client has stopped.");
                if (TotalConnectionsLocked() >= _options.MaxConnections ||
                    endpoint.Connections.Count + endpoint.ConnectingCount >= _options.MaxConnectionsPerEndpoint)
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
                    _client._runtimeContext.Codecs);
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
            }
        }

        private void EnsureReconnect(EndpointState endpoint)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _stopping) != 0 || endpoint.ReconnectTask is { IsCompleted: false } ||
                    endpoint.Connections.Count + endpoint.ConnectingCount >= 1)
                {
                    return;
                }
                endpoint.ReconnectTask = ReconnectAsync(endpoint);
                _client.TrackBackgroundTask(endpoint.ReconnectTask);
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
            var candidates = new SharpLinkEndpointCandidate[endpoints.Length];
            for (var index = 0; index < endpoints.Length; index++)
            {
                var endpoint = endpoints[index];
                candidates[index] = new SharpLinkEndpointCandidate(
                    endpoint.Configuration.Endpoint,
                    endpoint.ReadyConnections.Length,
                    endpoint.ActiveCallCount,
                    Generation: 1);
            }
            Volatile.Write(ref _readyEndpoints, endpoints);
            Volatile.Write(ref _selectionCandidates, candidates);
            if (endpoints.Length == 0)
                _client.ResetReadySignal();
        }

        private int SelectEndpoint(EndpointState[] endpoints, ulong excluded)
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
                    var candidates = Volatile.Read(ref _selectionCandidates);
                    return _selector.Select(new SharpLinkEndpointSelectionContext(candidates, excluded));
                }
                catch (Exception exception)
                {
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
            var firstCalls = (long)firstState.ActiveCallCount * secondState.ReadyConnections.Length;
            var secondCalls = (long)secondState.ActiveCallCount * firstState.ReadyConnections.Length;
            return firstCalls <= secondCalls ? first : second;
        }

        private static int SelectRandom(int length, ulong excluded, int availableCount)
        {
            if (availableCount <= 0)
                return -1;
            var target = Random.Shared.Next(availableCount);
            for (var index = 0; index < length; index++)
            {
                if ((excluded & (1UL << index)) != 0)
                    continue;
                if (target-- == 0)
                    return index;
            }
            return -1;
        }

        private int SelectRoundRobin(int length, ulong excluded)
        {
            var start = unchecked((uint)Interlocked.Increment(ref _roundRobinCursor));
            for (var offset = 0; offset < length; offset++)
            {
                var index = (int)((start + (uint)offset) % (uint)length);
                if ((excluded & (1UL << index)) == 0)
                    return index;
            }
            return -1;
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

        private EndpointState? FindEndpoint(ClientConnection connection)
        {
            lock (_gate)
            {
                for (var index = 0; index < _endpoints.Length; index++)
                    if (_endpoints[index].Connections.Contains(connection))
                        return _endpoints[index];
            }
            return null;
        }

        private int TotalConnectionsLocked()
        {
            var count = 0;
            for (var index = 0; index < _endpoints.Length; index++)
                count += _endpoints[index].Connections.Count + _endpoints[index].ConnectingCount;
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
            ClientConnection[] connections;
            Task[] workers;
            lock (_gate)
            {
                connections = [.. _endpoints.SelectMany(static endpoint => endpoint.Connections)];
                workers = [.. _endpoints.Select(static endpoint => endpoint.ReconnectTask).Where(static task => task is not null)!];
                for (var index = 0; index < _endpoints.Length; index++)
                    _endpoints[index].Connections.Clear();
                Volatile.Write(ref _readyEndpoints, []);
                Volatile.Write(ref _selectionCandidates, []);
            }
            var stopping = CreateConnectionClosedException("Client is stopping.");
            for (var index = 0; index < connections.Length; index++)
            {
                connections[index].Fail(stopping);
                await DisposeConnectionAsync(connections[index]).ConfigureAwait(false);
            }
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested) { }
            for (var index = 0; index < _endpoints.Length; index++)
                await _endpoints[index].Configuration.TransportFactory.DisposeAsync().ConfigureAwait(false);
        }

        private static async Task DisposeConnectionAsync(ClientConnection connection)
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) { }
        }

        private sealed class EndpointState(StaticEndpointConfiguration configuration, int index)
        {
            public StaticEndpointConfiguration Configuration { get; } = configuration;
            public int Index { get; } = index;
            public HashSet<ClientConnection> Connections { get; } = [];
            private ClientConnection[] _readyConnections = [];
            public ClientConnection[] ReadyConnections => Volatile.Read(ref _readyConnections);
            public int ConnectingCount { get; set; }
            public Task? ReconnectTask { get; set; }
            public int ActiveCallCount
            {
                get
                {
                    var connections = ReadyConnections;
                    var count = 0;
                    for (var index = 0; index < connections.Length; index++)
                        count += connections[index].ActiveCallCount;
                    return count;
                }
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
    }
}
