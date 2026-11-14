namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Owns the resolver-published endpoint generations and the immutable snapshot used by the
    /// dynamic-cluster selection hot path. Mutations are serialized by DynamicClusterRuntime's
    /// existing gate; readers only observe arrays published with volatile reads/writes.
    /// </summary>
    private sealed class DynamicClusterTopologyState : System.Collections.IEnumerable
    {
        private readonly EndpointSelectionPolicyState _selectionPolicy;
        private readonly Dictionary<string, DynamicEndpointState> _currentById = new(StringComparer.Ordinal);
        private readonly List<DynamicEndpointState> _allStates = [];
        private DynamicEndpointState[] _current = [];
        private DynamicEndpointState[] _readyEndpoints = [];
        private DynamicEndpointSelectionSnapshot _selectionSnapshot = DynamicEndpointSelectionSnapshot.Empty;
        private long _lastAcceptedVersion = -1;
        private long _nextGeneration;
        private int _roundRobinCursor;
        private int _leastPendingCursor;

        public DynamicClusterTopologyState(
            SharpLinkLoadBalancingStrategy strategy,
            ISharpLinkEndpointSelector? selector)
        {
            _selectionPolicy = new EndpointSelectionPolicyState(strategy, selector);
        }

        public DynamicEndpointState[] Current => _current;
        public IReadOnlyList<DynamicEndpointState> States => _allStates;
        public long LastAcceptedVersion => _lastAcceptedVersion;
        public int ReadyEndpointCount => Volatile.Read(ref _readyEndpoints).Length;
        public DynamicEndpointSelectionSnapshot SelectionSnapshot => Volatile.Read(ref _selectionSnapshot);
        public bool HasAcceptedEmptyTopology => _lastAcceptedVersion >= 0 && _current.Length == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EndpointSelectionPolicyGeneration CaptureSelectionPolicy()
            => _selectionPolicy.Capture();

        public SharpLinkEndpointSelectionPolicySnapshot GetEndpointSelectionPolicySnapshot()
            => _selectionPolicy.GetSnapshot();

        public void UpdateLoadBalancing(SharpLinkLoadBalancingStrategy strategy)
            => _selectionPolicy.PublishBuiltIn(strategy);

        public void UpdateEndpointSelector(ISharpLinkEndpointSelector selector)
            => _selectionPolicy.PublishCustom(selector);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => _current.GetEnumerator();

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

        public Dictionary<string, DynamicEndpointState> SnapshotCurrentById()
            => new(_currentById, StringComparer.Ordinal);

        public DynamicEndpointState CreateState(StaticEndpointConfiguration configuration)
            => new(configuration, Interlocked.Increment(ref _nextGeneration));

        public void AddState(DynamicEndpointState state) => _allStates.Add(state);

        public void RemoveState(DynamicEndpointState state) => _allStates.Remove(state);

        public void CommitCurrent(
            Dictionary<string, DynamicEndpointState> nextById,
            DynamicEndpointState[] current,
            long version)
        {
            _currentById.Clear();
            foreach (var pair in nextById)
                _currentById.Add(pair.Key, pair.Value);
            _current = current;
            _lastAcceptedVersion = version;
        }

        public bool IsCurrent(DynamicEndpointState endpoint)
            => _currentById.TryGetValue(endpoint.Configuration.Endpoint.Id, out var current) &&
               ReferenceEquals(current, endpoint);

        public int CountActiveCurrentInitialDials()
        {
            var count = 0;
            for (var index = 0; index < _current.Length; index++)
                count += _current[index].InitialDialReservations;
            return count;
        }

        public HashSet<IClientTransportFactory> GetOwnedFactories()
        {
            var factories = new HashSet<IClientTransportFactory>(ReferenceEqualityComparer.Instance);
            for (var index = 0; index < _allStates.Count; index++)
                factories.Add(_allStates[index].Configuration.TransportFactory);
            return factories;
        }

        public bool HasUniqueFactoryOwnership(IEnumerable<DynamicEndpointState> created)
        {
            var factories = GetOwnedFactories();
            foreach (var state in created)
            {
                if (!factories.Add(state.Configuration.TransportFactory))
                    return false;
            }
            return true;
        }

        public DynamicClusterReadinessSnapshot PublishReadySnapshot(bool force = false)
        {
            var ready = new List<DynamicEndpointState>(_current.Length);
            var readyConnections = 0;
            for (var index = 0; index < _current.Length; index++)
            {
                var endpoint = _current[index];
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
                Volatile.Write(ref _selectionSnapshot, new DynamicEndpointSelectionSnapshot(endpoints, candidates));
            }

            return new DynamicClusterReadinessSnapshot(
                _current.Length,
                endpoints.Length,
                readyConnections,
                changed);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SelectEndpoint(DynamicEndpointSelectionSnapshot snapshot, ulong excluded)
            => SelectEndpoint(snapshot, _selectionPolicy.Capture(), excluded);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SelectEndpoint(
            DynamicEndpointSelectionSnapshot snapshot,
            EndpointSelectionPolicyGeneration policy,
            ulong excluded)
        {
            return SelectEndpoint(snapshot.Candidates, policy, excluded);
        }

        internal int SelectEndpoint(SharpLinkEndpointCandidate[] candidates,
            EndpointSelectionPolicyGeneration policy, ulong excluded)
            => EndpointSelectionKernel.SelectEndpoint(candidates, policy, excluded,
                ref _roundRobinCursor, ref _leastPendingCursor);

        public static ClientConnection? SelectConnection(DynamicEndpointState endpoint)
            => EndpointSelectionKernel.SelectConnection(endpoint.ReadyConnections);

        public void Clear()
        {
            _allStates.Clear();
            _currentById.Clear();
            _current = [];
            Volatile.Write(ref _readyEndpoints, []);
            Volatile.Write(ref _selectionSnapshot, DynamicEndpointSelectionSnapshot.Empty);
        }

        private static bool HasSameMembership(DynamicEndpointState[] left, DynamicEndpointState[] right)
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

    internal sealed class DynamicEndpointState
    {
        private readonly Func<int> _readyConnectionCountProvider;
        private readonly Func<int> _activeCallCountProvider;
        private ClientConnection[] _readyConnections = [];

        public DynamicEndpointState(StaticEndpointConfiguration configuration, long generation)
        {
            Configuration = configuration;
            Generation = generation;
            _readyConnectionCountProvider = GetReadyConnectionCount;
            _activeCallCountProvider = GetActiveCallCount;
        }

        public StaticEndpointConfiguration Configuration { get; }
        public long Generation { get; }
        public ClientConnection[] ReadyConnections => Volatile.Read(ref _readyConnections);
        public Func<int> ReadyConnectionCountProvider => _readyConnectionCountProvider;
        public Func<int> ActiveCallCountProvider => _activeCallCountProvider;
        public int ConnectingCount { get; set; }
        public int InitialDialReservations { get; set; }
        public long ReconnectDelayTicks { get; set; }
        public long ReadyTimestamp { get; private set; }
        public bool HasReadyTimestamp { get; private set; }
        public bool Retiring { get; set; }
        public bool FactoryReleased { get; set; }
        public Task? ReconnectTask { get; set; }
        public Task? ExpansionTask { get; set; }

        public int ActiveCallCount => GetActiveCallCount();

        public void MarkReadyTimestamp(long timestamp)
        {
            if (HasReadyTimestamp)
                return;
            ReadyTimestamp = timestamp;
            HasReadyTimestamp = true;
        }

        public void ClearReadyTimestamp()
        {
            ReadyTimestamp = default;
            HasReadyTimestamp = false;
        }

        private int GetReadyConnectionCount() => ReadyConnections.Length;

        private int GetActiveCallCount()
        {
            var connections = ReadyConnections;
            var count = 0;
            for (var index = 0; index < connections.Length; index++)
                count += connections[index].ActiveCallCount;
            return count;
        }

        public void PublishReadyConnections(ClientConnection[] connections)
        {
            ArgumentNullException.ThrowIfNull(connections);
            Volatile.Write(ref _readyConnections, connections);
        }
    }

    private sealed class DynamicEndpointSelectionSnapshot(
        DynamicEndpointState[] endpoints,
        SharpLinkEndpointCandidate[] candidates)
    {
        public static readonly DynamicEndpointSelectionSnapshot Empty = new([], []);
        public DynamicEndpointState[] Endpoints { get; } = endpoints;
        public SharpLinkEndpointCandidate[] Candidates { get; } = candidates;
    }
}
