namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Owns the mutable connection collections for dynamic endpoint generations. Callers serialize
    /// mutations with <c>DynamicClusterRuntime</c>'s gate; published ready arrays remain lock-free
    /// snapshots consumed by topology selection.
    /// </summary>
    internal sealed class DynamicClusterConnectionState
    {
        private readonly Dictionary<DynamicEndpointState, HashSet<ClientConnection>> _connectionsByEndpoint = [];
        private readonly HashSet<ClientConnection> _retiringConnections = [];

        public int RetiringConnectionCount => _retiringConnections.Count;

        public void Add(DynamicEndpointState endpoint, ClientConnection connection)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(connection);
            if (!_connectionsByEndpoint.TryGetValue(endpoint, out var connections))
            {
                connections = [];
                _connectionsByEndpoint.Add(endpoint, connections);
            }
            connections.Add(connection);
        }

        public bool Remove(DynamicEndpointState endpoint, ClientConnection connection)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(connection);
            if (!_connectionsByEndpoint.TryGetValue(endpoint, out var connections) || !connections.Remove(connection))
                return false;
            _retiringConnections.Remove(connection);
            if (connections.Count == 0)
                _connectionsByEndpoint.Remove(endpoint);
            return true;
        }

        public DynamicEndpointState? FindEndpoint(
            IReadOnlyList<DynamicEndpointState> states,
            ClientConnection connection)
        {
            ArgumentNullException.ThrowIfNull(states);
            ArgumentNullException.ThrowIfNull(connection);
            for (var index = 0; index < states.Count; index++)
            {
                var endpoint = states[index];
                if (_connectionsByEndpoint.TryGetValue(endpoint, out var connections) && connections.Contains(connection))
                    return endpoint;
            }
            return null;
        }

        public int CountConnections(Func<ClientConnection, int> count)
        {
            ArgumentNullException.ThrowIfNull(count);
            var result = 0;
            foreach (var connections in _connectionsByEndpoint.Values)
                foreach (var connection in connections)
                    result += count(connection);
            return result;
        }

        public int NonRetiringConnectionCount(DynamicEndpointState endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            if (!_connectionsByEndpoint.TryGetValue(endpoint, out var connections))
                return 0;
            var count = 0;
            foreach (var connection in connections)
                if (connection.State == ClientConnectionState.Ready)
                    count++;
            return count;
        }

        public int TotalActiveConnections(IReadOnlyList<DynamicEndpointState> states)
        {
            ArgumentNullException.ThrowIfNull(states);
            var count = 0;
            for (var index = 0; index < states.Count; index++)
                count += NonRetiringConnectionCount(states[index]) + states[index].ConnectingCount;
            return count;
        }

        public bool IsRetiringBudgetExceeded(int maximumRetiringConnections)
            => _retiringConnections.Count > maximumRetiringConnections;

        public bool TryMarkDraining(
            IReadOnlyList<DynamicEndpointState> states,
            ClientConnection connection,
            out DynamicEndpointState? endpoint,
            out bool disposeNow)
        {
            endpoint = FindEndpoint(states, connection);
            if (endpoint is null)
            {
                disposeNow = false;
                return false;
            }

            connection.MarkDraining();
            if (connection.ActiveCallCount == 0)
            {
                Remove(endpoint, connection);
                disposeNow = true;
            }
            else
            {
                _retiringConnections.Add(connection);
                disposeNow = false;
            }
            return true;
        }

        public bool TryRetireDrainingIfIdle(
            IReadOnlyList<DynamicEndpointState> states,
            ClientConnection connection,
            out DynamicEndpointState? endpoint)
        {
            endpoint = null;
            if (connection.State != ClientConnectionState.Draining || connection.ActiveCallCount != 0)
                return false;
            endpoint = FindEndpoint(states, connection);
            return endpoint is not null && Remove(endpoint, connection);
        }

        public bool BeginEndpointRetirement(
            DynamicEndpointState endpoint,
            List<ClientConnection> connectionsToDispose)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(connectionsToDispose);
            if (endpoint.Retiring)
                return false;
            endpoint.Retiring = true;
            if (!_connectionsByEndpoint.TryGetValue(endpoint, out var connections) || connections.Count == 0)
                return true;

            var snapshot = connections.ToArray();
            for (var index = 0; index < snapshot.Length; index++)
            {
                var connection = snapshot[index];
                connection.MarkDraining();
                if (connection.ActiveCallCount == 0)
                {
                    Remove(endpoint, connection);
                    connectionsToDispose.Add(connection);
                }
                else
                {
                    _retiringConnections.Add(connection);
                }
            }
            return true;
        }

        public bool CanRelease(DynamicEndpointState endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            return endpoint.ConnectingCount == 0 &&
                   (!_connectionsByEndpoint.TryGetValue(endpoint, out var connections) || connections.Count == 0);
        }

        public void ReleaseEndpoint(DynamicEndpointState endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            if (_connectionsByEndpoint.TryGetValue(endpoint, out var connections) && connections.Count != 0)
                throw new InvalidOperationException("A dynamic endpoint cannot be released while it still owns connections.");
            _connectionsByEndpoint.Remove(endpoint);
        }

        public void PublishReadyConnections(IReadOnlyList<DynamicEndpointState> endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            for (var index = 0; index < endpoints.Count; index++)
            {
                var endpoint = endpoints[index];
                if (!_connectionsByEndpoint.TryGetValue(endpoint, out var connections) || connections.Count == 0)
                {
                    endpoint.PublishReadyConnections([]);
                    continue;
                }

                var ready = new List<ClientConnection>(connections.Count);
                foreach (var connection in connections)
                    if (connection.CanAcceptCalls)
                        ready.Add(connection);
                endpoint.PublishReadyConnections(ready.ToArray());
            }
        }

        public ClientConnection[] DetachAll(IReadOnlyList<DynamicEndpointState> states)
        {
            ArgumentNullException.ThrowIfNull(states);
            var connections = new List<ClientConnection>();
            for (var index = 0; index < states.Count; index++)
            {
                if (_connectionsByEndpoint.TryGetValue(states[index], out var owned))
                    connections.AddRange(owned);
            }
            _connectionsByEndpoint.Clear();
            _retiringConnections.Clear();
            return connections.ToArray();
        }
    }
}
