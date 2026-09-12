namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Owns dynamic-endpoint reconnect admission, refill scheduling and per-generation backoff.
    /// Mutations are serialized by DynamicClusterRuntime's gate.
    /// </summary>
    private sealed class DynamicClusterReconnectCoordinator
    {
        private readonly SharpLinkClient _client;
        private readonly Lock _gate;
        private readonly SharpLinkClusterOptions _options;
        private readonly DynamicClusterTopologyState _current;
        private readonly DynamicClusterConnectionState _connections;
        private readonly Func<bool> _isStopping;
        private readonly Func<DynamicEndpointState, CancellationToken, Task> _connectOneAsync;
        private readonly Action<Task, string> _trackTask;
        private int _reconnectCursor;

        public DynamicClusterReconnectCoordinator(
            SharpLinkClient client,
            Lock gate,
            SharpLinkClusterOptions options,
            DynamicClusterTopologyState current,
            DynamicClusterConnectionState connections,
            Func<bool> isStopping,
            Func<DynamicEndpointState, CancellationToken, Task> connectOneAsync,
            Action<Task, string> trackTask)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _current = current ?? throw new ArgumentNullException(nameof(current));
            _connections = connections ?? throw new ArgumentNullException(nameof(connections));
            _isStopping = isStopping ?? throw new ArgumentNullException(nameof(isStopping));
            _connectOneAsync = connectOneAsync ?? throw new ArgumentNullException(nameof(connectOneAsync));
            _trackTask = trackTask ?? throw new ArgumentNullException(nameof(trackTask));
        }

        public void EnsureMinimumReadyEndpoints()
        {
            List<DynamicEndpointState>? missing = null;
            lock (_gate)
            {
                if (_isStopping())
                    return;

                var current = _current.Current;
                var target = Math.Min(_options.MinReadyEndpoints, current.Length);
                var availableCapacity = _options.MaxConnections - TotalActiveConnectionsLocked();
                var activeReconnects = current.Count(static endpoint => endpoint.ReconnectTask is { IsCompleted: false });
                var activeInitialDials = _current.CountActiveCurrentInitialDials();
                var remaining = Math.Min(
                    target - _current.ReadyEndpointCount - activeReconnects - activeInitialDials,
                    availableCapacity);
                var start = unchecked((uint)Interlocked.Increment(ref _reconnectCursor));
                for (var offset = 0; remaining > 0 && offset < current.Length; offset++)
                {
                    var index = (int)((start + (uint)offset) % (uint)current.Length);
                    var endpoint = current[index];
                    if (endpoint.ReadyConnections.Length != 0 ||
                        _connections.NonRetiringConnectionCount(endpoint) + endpoint.ConnectingCount != 0 ||
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

        public void EnsureReconnect(DynamicEndpointState endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            lock (_gate)
            {
                var current = _current.Current;
                var target = Math.Min(_options.MinReadyEndpoints, current.Length);
                var activeReconnects = current.Count(static candidate => candidate.ReconnectTask is { IsCompleted: false });
                if (endpoint.ReconnectTask is { IsCompleted: false } || !NeedsReconnectLocked(endpoint) ||
                    activeReconnects >= target - _current.ReadyEndpointCount)
                {
                    return;
                }

                endpoint.ReconnectTask = ReconnectAsync(endpoint);
                _trackTask(endpoint.ReconnectTask, "DynamicClusterReconnect");
            }
        }

        private async Task ReconnectAsync(DynamicEndpointState endpoint)
        {
            try
            {
                var complete = false;
                while (!complete)
                {
                    ReconnectPolicyGeneration generation;
                    TimeSpan baseDelay;
                    lock (_gate)
                    {
                        if (!NeedsReconnectLocked(endpoint))
                            break;
                        generation = _client.CaptureReconnectPolicy();
                        baseDelay = ResolveReconnectDelay(endpoint.ReconnectDelayTicks, generation.Policy);
                    }

                    try
                    {
                        if (!await _client.WaitForReconnectDelayAsync(
                                baseDelay, generation, _client._shutdownCts.Token).ConfigureAwait(false))
                        {
                            continue;
                        }

                        bool shouldConnect;
                        lock (_gate)
                            shouldConnect = NeedsReconnectLocked(endpoint);
                        if (!shouldConnect)
                            break;

                        SharpLinkTelemetry.ReconnectAttempt();
                        await _connectOneAsync(endpoint, _client._shutdownCts.Token).ConfigureAwait(false);
                        var completionPolicy = generation.Policy;
                        lock (_gate)
                        {
                            if (_client.IsCurrentReconnectPolicyGeneration(generation))
                            {
                                endpoint.ReconnectDelayTicks = ResolveReconnectCompletionDelay(
                                    baseDelay,
                                    endpoint.ReadyConnections.Length != 0,
                                    completionPolicy).Ticks;
                            }
                        }
                        complete = true;
                    }
                    catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        LogClientConnectionAttemptFailed(_client._logger, nameof(ReconnectAsync), exception);
                        var completionPolicy = generation.Policy;
                        lock (_gate)
                        {
                            if (_client.IsCurrentReconnectPolicyGeneration(generation))
                            {
                                endpoint.ReconnectDelayTicks = ResolveReconnectCompletionDelay(
                                    baseDelay,
                                    reconnected: false,
                                    completionPolicy).Ticks;
                            }
                        }
                        complete = true;
                    }
                }
            }
            finally
            {
                lock (_gate)
                    endpoint.ReconnectTask = null;
            }

            if (!_isStopping() && !_client._shutdownCts.IsCancellationRequested)
                EnsureMinimumReadyEndpoints();
        }

        private bool NeedsReconnectLocked(DynamicEndpointState endpoint)
            => !_isStopping() && !_client._shutdownCts.IsCancellationRequested &&
               !endpoint.Retiring && _current.IsCurrent(endpoint) &&
               !_connections.IsRetiringBudgetExceeded(_options.MaxRetiringConnections) &&
               _current.ReadyEndpointCount < Math.Min(_options.MinReadyEndpoints, _current.Current.Length) &&
               TotalActiveConnectionsLocked() < _options.MaxConnections &&
               _connections.NonRetiringConnectionCount(endpoint) + endpoint.ConnectingCount == 0;

        private int TotalActiveConnectionsLocked()
            => _connections.TotalActiveConnections(_current.States);
    }
}
