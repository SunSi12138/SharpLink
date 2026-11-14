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
        private readonly EndpointSelectionPolicyState _selectionPolicy;
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
            _selectionPolicy = new EndpointSelectionPolicyState(strategy, selector);
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EndpointSelectionPolicyGeneration CaptureSelectionPolicy()
            => _selectionPolicy.Capture();

        public SharpLinkEndpointSelectionPolicySnapshot GetEndpointSelectionPolicySnapshot()
            => _selectionPolicy.GetSnapshot();

        public void UpdateLoadBalancing(SharpLinkLoadBalancingStrategy strategy)
            => _selectionPolicy.PublishBuiltIn(strategy);

        public void UpdateEndpointSelector(ISharpLinkEndpointSelector selector)
            => _selectionPolicy.PublishCustom(selector);

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SelectEndpoint(StaticEndpointSelectionSnapshot snapshot, ulong excluded)
            => SelectEndpoint(snapshot, _selectionPolicy.Capture(), excluded);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SelectEndpoint(
            StaticEndpointSelectionSnapshot snapshot,
            EndpointSelectionPolicyGeneration policy,
            ulong excluded)
        {
            return SelectEndpoint(snapshot.Candidates, policy, excluded);
        }

        internal int SelectEndpoint(SharpLinkEndpointCandidate[] candidates,
            EndpointSelectionPolicyGeneration policy, ulong excluded)
        {
            try
            {
                return EndpointSelectionKernel.SelectEndpoint(candidates, policy, excluded,
                    ref _roundRobinCursor, ref _leastPendingCursor);
            }
            catch (Exception exception) when (policy.HasCustomSelector)
            {
                _logger?.LogError(exception, "SharpLink endpoint selector failed.");
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                    "The endpoint selector failed.", exception);
            }
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
