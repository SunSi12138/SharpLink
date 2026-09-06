namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Owns static-cluster ready endpoint publication and endpoint-selection state. Mutations are
    /// serialized by <c>StaticClusterRuntime</c>'s existing gate; the RPC selection path only reads
    /// immutable snapshots published through volatile writes.
    /// </summary>
    internal sealed class StaticClusterTopologyState
    {
        private readonly SharpLinkLoadBalancingStrategy _strategy;
        private readonly ISharpLinkEndpointSelector? _selector;
        private readonly ILogger? _logger;
        private StaticClientRuntimeEndpointState[] _readyEndpoints = [];
        private StaticEndpointSelectionSnapshot _selectionSnapshot = StaticEndpointSelectionSnapshot.Empty;
        private int _roundRobinCursor;
        private int _leastPendingCursor;

        public StaticClusterTopologyState(
            SharpLinkLoadBalancingStrategy strategy,
            ISharpLinkEndpointSelector? selector,
            ILogger? logger = null)
        {
            _strategy = strategy;
            _selector = selector;
            _logger = logger;
        }

        public int ReadyEndpointCount => Volatile.Read(ref _readyEndpoints).Length;

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

        public StaticEndpointSelectionSnapshot SelectionSnapshot
            => Volatile.Read(ref _selectionSnapshot);

        public StaticClusterReadinessSnapshot PublishReadySnapshot(
            IReadOnlyList<StaticClientRuntimeEndpointState> endpointStates)
        {
            ArgumentNullException.ThrowIfNull(endpointStates);
            var ready = new List<StaticClientRuntimeEndpointState>(endpointStates.Count);
            var readyConnections = 0;
            for (var index = 0; index < endpointStates.Count; index++)
            {
                var endpoint = endpointStates[index];
                var endpointReadyConnections = endpoint.ReadyConnections.Length;
                if (endpointReadyConnections == 0)
                    continue;

                ready.Add(endpoint);
                readyConnections += endpointReadyConnections;
            }

            var endpoints = ready.ToArray();
            var existing = Volatile.Read(ref _readyEndpoints);
            var changed = !HasSameMembership(existing, endpoints);
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
                        generation: 1);
                }
                Volatile.Write(ref _readyEndpoints, endpoints);
                Volatile.Write(ref _selectionSnapshot, new StaticEndpointSelectionSnapshot(endpoints, candidates));
            }

            return new StaticClusterReadinessSnapshot(
                ReadyEndpoints: endpoints.Length,
                ReadyConnections: readyConnections,
                ReadyEndpointDelta: endpoints.Length - existing.Length,
                MembershipChanged: changed);
        }

        public int Clear()
        {
            var previousReadyEndpointCount = Volatile.Read(ref _readyEndpoints).Length;
            Volatile.Write(ref _readyEndpoints, []);
            Volatile.Write(ref _selectionSnapshot, StaticEndpointSelectionSnapshot.Empty);
            return previousReadyEndpointCount;
        }

        public int SelectEndpoint(StaticEndpointSelectionSnapshot snapshot, ulong excluded)
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
            {
                try
                {
                    return _selector.Select(new SharpLinkEndpointSelectionContext(snapshot.Candidates, excluded));
                }
                catch (Exception exception)
                {
                    _logger?.LogError(exception, "SharpLink endpoint selector failed.");
                    throw new SharpLinkException(
                        SharpLinkErrorCode.FailedPrecondition,
                        "The endpoint selector failed.",
                        exception);
                }
            }
            return _strategy switch
            {
                SharpLinkLoadBalancingStrategy.Random => SelectRandom(endpoints.Length, excluded, availableCount),
                SharpLinkLoadBalancingStrategy.RoundRobin => EndpointSelectionKernel.SelectRoundRobinIndex(
                    ref _roundRobinCursor, endpoints.Length, excluded),
                SharpLinkLoadBalancingStrategy.LeastPending => SelectLeastPending(endpoints, excluded),
                _ => SelectPowerOfTwo(endpoints, excluded, availableCount)
            };
        }

        private int SelectPowerOfTwo(
            StaticClientRuntimeEndpointState[] endpoints,
            ulong excluded,
            int availableCount)
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
            => availableCount <= 0
                ? -1
                : EndpointSelectionKernel.SelectRandomIndex(
                    length,
                    excluded,
                    availableCount,
                    Random.Shared.Next(availableCount));

        private int SelectLeastPending(StaticClientRuntimeEndpointState[] endpoints, ulong excluded)
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

        private static bool HasSameMembership(
            StaticClientRuntimeEndpointState[] left,
            StaticClientRuntimeEndpointState[] right)
        {
            if (left.Length != right.Length)
                return false;
            for (var index = 0; index < left.Length; index++)
                if (!ReferenceEquals(left[index], right[index]))
                    return false;
            return true;
        }
    }

    internal readonly record struct StaticClusterReadinessSnapshot(
        int ReadyEndpoints,
        int ReadyConnections,
        int ReadyEndpointDelta,
        bool MembershipChanged);

    internal sealed class StaticEndpointSelectionSnapshot(
        StaticClientRuntimeEndpointState[] endpoints,
        SharpLinkEndpointCandidate[] candidates)
    {
        public static readonly StaticEndpointSelectionSnapshot Empty = new([], []);
        public StaticClientRuntimeEndpointState[] Endpoints { get; } = endpoints;
        public SharpLinkEndpointCandidate[] Candidates { get; } = candidates;
    }
}
