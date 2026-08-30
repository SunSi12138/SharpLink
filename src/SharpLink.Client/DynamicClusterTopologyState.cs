namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Owns the resolver-published endpoint generations and the immutable snapshot used by the
    /// dynamic-cluster selection hot path. Mutations are serialized by DynamicClusterRuntime's
    /// existing gate; readers only observe arrays published with volatile reads/writes.
    /// </summary>
    private sealed class DynamicClusterTopologyState
    {
        private readonly SharpLinkLoadBalancingStrategy _strategy;
        private readonly ISharpLinkEndpointSelector? _selector;
        private readonly Dictionary<string, EndpointState> _currentById = new(StringComparer.Ordinal);
        private readonly List<EndpointState> _allStates = [];
        private EndpointState[] _current = [];
        private EndpointState[] _readyEndpoints = [];
        private EndpointSelectionSnapshot _selectionSnapshot = EndpointSelectionSnapshot.Empty;
        private long _lastAcceptedVersion = -1;
        private long _nextGeneration;
        private int _roundRobinCursor;
        private int _leastPendingCursor;

        public DynamicClusterTopologyState(
            SharpLinkLoadBalancingStrategy strategy,
            ISharpLinkEndpointSelector? selector)
        {
            _strategy = strategy;
            _selector = selector;
        }

        public EndpointState[] Current => _current;
        public IReadOnlyList<EndpointState> States => _allStates;
        public long LastAcceptedVersion => _lastAcceptedVersion;
        public int ReadyEndpointCount => Volatile.Read(ref _readyEndpoints).Length;
        public EndpointSelectionSnapshot SelectionSnapshot => Volatile.Read(ref _selectionSnapshot);
        public bool HasAcceptedEmptyTopology => _lastAcceptedVersion >= 0 && _current.Length == 0;
        public bool HasCustomSelector => _selector is not null;

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

        public Dictionary<string, EndpointState> SnapshotCurrentById()
            => new(_currentById, StringComparer.Ordinal);

        public EndpointState CreateState(StaticEndpointConfiguration configuration)
            => new(configuration, Interlocked.Increment(ref _nextGeneration));

        public void AddState(EndpointState state) => _allStates.Add(state);

        public void RemoveState(EndpointState state) => _allStates.Remove(state);

        public void CommitCurrent(
            Dictionary<string, EndpointState> nextById,
            EndpointState[] current,
            long version)
        {
            _currentById.Clear();
            foreach (var pair in nextById)
                _currentById.Add(pair.Key, pair.Value);
            _current = current;
            _lastAcceptedVersion = version;
        }

        public EndpointState? FindEndpoint(ClientConnection connection)
        {
            for (var index = 0; index < _allStates.Count; index++)
                if (_allStates[index].Connections.Contains(connection))
                    return _allStates[index];
            return null;
        }

        public bool IsCurrent(EndpointState endpoint)
            => _currentById.TryGetValue(endpoint.Configuration.Endpoint.Id, out var current) &&
               ReferenceEquals(current, endpoint);

        public DynamicClusterReadinessSnapshot PublishReadySnapshot(bool force = false)
        {
            var ready = new List<EndpointState>(_current.Length);
            var readyConnections = 0;
            for (var index = 0; index < _current.Length; index++)
            {
                var endpoint = _current[index];
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
            var changed = force || !HasSameMembership(existing, endpoints);
            if (changed)
            {
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
            }

            return new DynamicClusterReadinessSnapshot(
                _current.Length,
                endpoints.Length,
                readyConnections,
                changed);
        }

        public int SelectEndpoint(EndpointSelectionSnapshot snapshot, ulong excluded)
        {
            var endpoints = snapshot.Endpoints;
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
                return _selector.Select(new SharpLinkEndpointSelectionContext(snapshot.Candidates, excluded));
            return _strategy switch
            {
                SharpLinkLoadBalancingStrategy.Random => SelectRandom(endpoints.Length, excluded, availableCount),
                SharpLinkLoadBalancingStrategy.RoundRobin => EndpointSelectionKernel.SelectRoundRobinIndex(
                    ref _roundRobinCursor, endpoints.Length, excluded),
                SharpLinkLoadBalancingStrategy.LeastPending => SelectLeastPending(endpoints, excluded),
                _ => SelectPowerOfTwo(endpoints, excluded, availableCount)
            };
        }

        public static ClientConnection? SelectConnection(EndpointState endpoint)
            => EndpointSelectionKernel.SelectConnection(endpoint.ReadyConnections);

        public void Clear()
        {
            _allStates.Clear();
            _currentById.Clear();
            _current = [];
            Volatile.Write(ref _readyEndpoints, []);
            Volatile.Write(ref _selectionSnapshot, EndpointSelectionSnapshot.Empty);
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
                firstState.ActiveCallCount, firstState.ReadyConnections.Length,
                secondState.ActiveCallCount, secondState.ReadyConnections.Length) <= 0 ? first : second;
        }

        private static int SelectRandom(int length, ulong excluded, int availableCount)
            => availableCount <= 0 ? -1 : EndpointSelectionKernel.SelectRandomIndex(
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

        private static bool HasSameMembership(EndpointState[] left, EndpointState[] right)
        {
            if (left.Length != right.Length)
                return false;
            for (var index = 0; index < left.Length; index++)
                if (!ReferenceEquals(left[index], right[index]))
                    return false;
            return true;
        }
    }

    private readonly record struct DynamicClusterReadinessSnapshot(
        int ActiveEndpoints,
        int ReadyEndpoints,
        int ReadyConnections,
        bool MembershipChanged);

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
