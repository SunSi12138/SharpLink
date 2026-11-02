namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Owns the resolver-published endpoint generations and the immutable snapshot used by the
    /// dynamic-cluster selection hot path. Mutations are serialized by DynamicClusterRuntime's
    /// existing gate; readers only observe arrays published with volatile reads/writes.
    /// </summary>
    private sealed class DynamicClusterTopologyState : System.Collections.IEnumerable, IContractRouteSelection
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
        public int ReadyEndpointCount
        {
            get
            {
                var count = 0;
                foreach (var endpoint in Volatile.Read(ref _readyEndpoints))
                    if (endpoint.HasReadyConnection) count++;
                return count;
            }
        }
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
                    count += endpoints[index].LiveReadyConnectionCount;
                return count;
            }
        }

        public Dictionary<string, DynamicEndpointState> SnapshotCurrentById()
            => new(_currentById, StringComparer.Ordinal);

        public DynamicEndpointState CreateState(StaticEndpointConfiguration configuration)
            => new(configuration, Interlocked.Increment(ref _nextGeneration));

        public void AddState(DynamicEndpointState state) => _allStates.Add(state);

        public void RemoveState(DynamicEndpointState state) => _allStates.Remove(state);

        public void PrepareCommitCapacity(int endpointCount, int addedStates)
        {
            _currentById.EnsureCapacity(endpointCount);
            _allStates.EnsureCapacity(_allStates.Count + addedStates);
        }

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

        internal sealed record ReadyPublication(
            DynamicEndpointState[] Endpoints, DynamicEndpointSelectionSnapshot Snapshot,
            DynamicClusterReadinessSnapshot Readiness);

        public DynamicClusterReadinessSnapshot PublishReadySnapshot(bool force = false)
        {
            var arrays = new ClientConnection[_current.Length][];
            for (var index = 0; index < arrays.Length; index++) arrays[index] = _current[index].ReadyConnections;
            var publication = PrepareReadySnapshot(arrays, force);
            CommitReadySnapshot(publication);
            return publication.Readiness;
        }

        internal ReadyPublication PrepareReadySnapshot(ClientConnection[][] arrays, bool force = false,
            DynamicEndpointState[]? prospective = null, SharpLinkEndpoint[]? definitions = null)
        {
            var current = prospective ?? _current;
            var ready = new List<DynamicEndpointState>(current.Length);
            var connectionCount = 0;
            for (var index = 0; index < current.Length; index++)
            {
                if (arrays[index].Length == 0) continue;
                ready.Add(current[index]);
                connectionCount += arrays[index].Length;
            }
            var endpoints = ready.ToArray();
            var changed = force || !HasSameMembership(Volatile.Read(ref _readyEndpoints), endpoints);
            var snapshot = _selectionSnapshot;
            if (changed)
            {
                var candidates = new SharpLinkEndpointCandidate[endpoints.Length];
                for (var index = 0; index < endpoints.Length; index++)
                {
                    var endpoint = endpoints[index];
                    var definition = definitions is null ? endpoint.Configuration.Endpoint : definitions[Array.IndexOf(current, endpoint)];
                    candidates[index] = new SharpLinkEndpointCandidate(definition,
                        endpoint.ReadyConnectionCountProvider, endpoint.ActiveCallCountProvider, endpoint.Generation);
                }
                snapshot = new DynamicEndpointSelectionSnapshot(endpoints, candidates);
            }
            return new ReadyPublication(endpoints, snapshot,
                new DynamicClusterReadinessSnapshot(current.Length, endpoints.Length, connectionCount, changed));
        }

        internal void CommitReadySnapshot(ReadyPublication publication)
        {
            Volatile.Write(ref _readyEndpoints, publication.Endpoints);
            Volatile.Write(ref _selectionSnapshot, publication.Snapshot);
        }

        public int SelectContractEndpoint(ContractRouteView view, EndpointSelectionPolicyGeneration policy,
            ReadOnlySpan<int> capacities, ulong excluded)
            => SelectContractEndpointCore(view, policy, capacities, excluded, ref _roundRobinCursor, ref _leastPendingCursor);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SelectEndpoint(DynamicEndpointSelectionSnapshot snapshot, ulong excluded)
            => SelectEndpoint(snapshot, _selectionPolicy.Capture(), excluded);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SelectEndpoint(
            DynamicEndpointSelectionSnapshot snapshot,
            EndpointSelectionPolicyGeneration policy,
            ulong excluded)
        {
            var endpoints = snapshot.Endpoints;
            var availableCount = 0;
            for (var index = 0; index < endpoints.Length; index++)
                availableCount += (excluded & (1UL << index)) == 0 ? 1 : 0;
            if (availableCount == 0)
                return -1;
            if (availableCount == 1 && !policy.HasCustomSelector)
            {
                for (var index = 0; index < endpoints.Length; index++)
                    if ((excluded & (1UL << index)) == 0)
                        return index;
            }
            if (policy.Selector is { } selector)
                return selector.Select(new SharpLinkEndpointSelectionContext(snapshot.Candidates, excluded));
            return policy.Strategy switch
            {
                SharpLinkLoadBalancingStrategy.Random => SelectRandom(endpoints.Length, excluded, availableCount),
                SharpLinkLoadBalancingStrategy.RoundRobin => EndpointSelectionKernel.SelectRoundRobinIndex(
                    ref _roundRobinCursor, endpoints.Length, excluded),
                SharpLinkLoadBalancingStrategy.LeastPending => SelectLeastPending(endpoints, excluded),
                _ => SelectPowerOfTwo(endpoints, excluded, availableCount)
            };
        }

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

        private int SelectPowerOfTwo(DynamicEndpointState[] endpoints, ulong excluded, int availableCount)
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

        private int SelectLeastPending(DynamicEndpointState[] endpoints, ulong excluded)
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

        internal int LiveReadyConnectionCount
        {
            get
            {
                var count = 0;
                foreach (var connection in ReadyConnections)
                    if (connection.CanAcceptCalls) count++;
                return count;
            }
        }
        internal bool HasReadyConnection => LiveReadyConnectionCount != 0;

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
