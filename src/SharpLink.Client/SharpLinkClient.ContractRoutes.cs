namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private bool _contractRoutesStopped;
    private ContractRouteTopology _contractRouteTopology = ContractRouteTopology.Empty;
    private WeakReference<ContractCallSite>[] _contractRouteDemands = [];

    // Cold-only observation/fault points. Never consulted by selection or reservation.
    internal Action<ContractCallSite, string>? ContractRouteBuildForTesting { get; set; }
    internal Action<string>? ContractRoutePublicationForTesting { get; set; }

    internal interface IContractRouteSelection
    {
        EndpointSelectionPolicyGeneration CaptureSelectionPolicy();
        int SelectContractEndpoint(ContractRouteView view, EndpointSelectionPolicyGeneration policy,
            ReadOnlySpan<int> capacities, ulong excluded);
    }

    internal sealed class ContractRouteEndpoint(
        object identity, SharpLinkEndpoint endpoint, long generation, ClientConnection[] connections,
        Func<int> activeCalls, Action<ClientConnection> selected,
        Action<SharpLinkEndpointCandidate>? retireAdmission = null)
    {
        internal object Identity { get; } = identity;
        internal SharpLinkEndpoint Endpoint { get; } = endpoint;
        internal long Generation { get; } = generation;
        internal ClientConnection[] Connections { get; } = connections;
        internal Func<int> ActiveCalls { get; } = activeCalls;
        internal Action<ClientConnection> Selected { get; } = selected;
        internal Action<SharpLinkEndpointCandidate>? RetireAdmission { get; } = retireAdmission;
    }

    internal sealed class ContractRouteTopology(
        ulong revision, object indexMap, ContractRouteEndpoint[] endpoints,
        IContractRouteSelection? selection, bool terminal = false)
    {
        internal static readonly ContractRouteTopology Empty = new(0, new object(), [], null);
        internal static readonly ContractRouteTopology Terminal = new(ulong.MaxValue, new object(), [], null, true);
        internal ulong Revision { get; } = revision;
        internal object IndexMap { get; } = indexMap;
        internal ContractRouteEndpoint[] Endpoints { get; } = endpoints;
        internal IContractRouteSelection? Selection { get; } = selection;
        internal bool IsTerminal { get; } = terminal;
    }

    // A candidate may escape to a user policy. This object must not point back to its site,
    // local contract, registry, or the enclosing view.
    internal sealed class ContractRouteBucket(ContractRouteEndpoint endpoint, ContractCallSite.BoundTarget[] targets)
    {
        internal ContractRouteEndpoint Endpoint { get; } = endpoint;
        internal ContractCallSite.BoundTarget[] Targets { get; } = targets;
        internal int EligibleCount()
        {
            var count = 0;
            foreach (var target in Targets)
                if (target.IsEligible) count++;
            return count;
        }
    }

    internal sealed class ContractRouteView(
        ContractRouteTopology topology, ulong bindingRevision, ContractCallSite.BoundTarget[] targets,
        ContractRouteBucket[] buckets, SharpLinkEndpointCandidate[] candidates)
    {
        internal static readonly ContractRouteView Empty = new(ContractRouteTopology.Empty, 0, [], [], []);
        internal ContractRouteTopology Topology { get; } = topology;
        internal ulong BindingRevision { get; } = bindingRevision;
        internal ContractCallSite.BoundTarget[] Targets { get; } = targets;
        internal ContractRouteBucket[] Buckets { get; } = buckets;
        internal SharpLinkEndpointCandidate[] Candidates { get; } = candidates;
        internal bool HasEligibleTarget()
        {
            foreach (var bucket in Buckets)
                if (bucket.EligibleCount() != 0) return true;
            return false;
        }
    }

    // Not an AggregateException/OCE: ordinary supervision must not flatten this stage graph
    // or mistake a route-construction OCE for expected shutdown cancellation.
    internal sealed class ContractRouteFailure : Exception
    {
        private readonly Exception?[] _errors;
        private readonly string?[] _stages;
        private int _count;
        internal ContractRouteFailure(int capacity = 4104)
            : base("Contract route reconciliation failed; original stage failures are retained.")
        {
            _errors = new Exception?[capacity];
            _stages = new string?[capacity];
        }
        internal bool TopologyCommitted { get; set; }
        internal int Count => _count;
        internal Exception ErrorAt(int index) => _errors[index]!;
        internal string StageAt(int index) => _stages[index]!;
        internal void Add(string stage, Exception error)
        {
            // Capacity is preflighted for one <=4096-site fanout plus fixed owner stages.
            if (_count == _errors.Length)
                throw new InvalidOperationException("Contract route failure capacity was exceeded.", error);
            _stages[_count] = stage;
            _errors[_count++] = error;
        }
        internal void ThrowIfAny()
        {
            if (_count != 0) throw this;
        }
    }

    private static bool HaveSameContractConnections(ClientConnection[] left, ClientConnection[] right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (!ReferenceEquals(left[index], right[index])) return false;
        return true;
    }

    private ContractRouteTopology BuildContractRouteTopology(
        ContractRouteEndpoint[] endpoints, IContractRouteSelection selection)
    {
        if (endpoints.Length > SharpLinkClusterOptions.MaximumEndpoints ||
            endpoints.Sum(static endpoint => endpoint.Connections.Length) > SharpLinkConnectionPoolOptions.MaximumConnections)
            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "Contract route topology capacity exceeded.");
        var current = Volatile.Read(ref _contractRouteTopology);
        if (current.IsTerminal) throw RouteRevisionExhausted();
        var sameIndices = current.Endpoints.Length == endpoints.Length;
        var sameMembers = sameIndices;
        for (var index = 0; index < endpoints.Length && sameIndices; index++)
        {
            var left = current.Endpoints[index];
            var right = endpoints[index];
            sameIndices = ReferenceEquals(left.Identity, right.Identity) &&
                ReferenceEquals(left.Endpoint, right.Endpoint) && left.Generation == right.Generation;
            sameMembers &= sameIndices && HaveSameContractConnections(left.Connections, right.Connections);
        }
        if (sameMembers) return current;
        if (current.Revision == ulong.MaxValue - 1)
        {
            Volatile.Write(ref _contractRouteTopology, ContractRouteTopology.Terminal);
            foreach (var weak in Volatile.Read(ref _contractRouteDemands))
                if (weak.TryGetTarget(out var site)) site.ExhaustContractRouteRevision();
            throw RouteRevisionExhausted();
        }
        return new ContractRouteTopology(current.Revision + 1,
            sameIndices ? current.IndexMap : new object(), endpoints, selection);
    }

    private void CommitContractRouteTopology(ContractRouteTopology topology, ContractRouteFailure failures)
    {
        if (ReferenceEquals(Volatile.Read(ref _contractRouteTopology), topology)) return;
        Volatile.Write(ref _contractRouteTopology, topology);
        try { ContractRoutePublicationForTesting?.Invoke("topology-published"); }
        catch (Exception error) { failures.Add("topology-observer", error); }
        // Publication precedes capture; registration publishes its weak entry before reconcile.
        var demands = Volatile.Read(ref _contractRouteDemands);
        foreach (var weak in demands)
        {
            if (!weak.TryGetTarget(out var site)) continue;
            try { site.ReconcileContractRoute(); }
            catch (Exception error) { failures.Add("site-reconcile", error); }
        }
    }

    private void PublishContractDemandSnapshotLocked()
        => Volatile.Write(ref _contractRouteDemands, _contractDemands.ToArray());

    private void ClearContractRoutingForStop()
    {
        Volatile.Write(ref _contractRoutesStopped, true);
        foreach (var weak in Volatile.Read(ref _contractRouteDemands))
            if (weak.TryGetTarget(out var site)) site.ClearContractRouteForStop();
    }

    private static SharpLinkException RouteRevisionExhausted()
        => new(SharpLinkErrorCode.ResourceExhausted, "Contract route revision capacity is exhausted.");

    private static async ValueTask DisposeConnectionWithRouteFailuresAsync(ClientConnection connection)
    {
        var routeFailure = connection.TakeContractRouteFailures();
        if (routeFailure is null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }
        Task? original = null;
        try { original = connection.DisposeAsync().AsTask(); }
        catch (Exception error) { routeFailure.Add("connection-dispose-start", error); }
        if (original is null)
        {
            // A synchronous Dispose start failure cannot substitute for observing physical session exit.
            try { original = connection.Session.DisposeAsync().AsTask(); }
            catch (Exception error) { routeFailure.Add("session-dispose-start", error); }
        }
        if (original is not null)
        {
            try { await original.ConfigureAwait(false); }
            catch (Exception error)
            {
                routeFailure.Add("connection-dispose", original.Exception is { } graph ? graph : error);
            }
        }
        routeFailure.ThrowIfAny();
    }

    internal sealed partial class ContractCallSite
    {
        private ContractRouteView _contractRouteView = ContractRouteView.Empty;
        private ulong _bindingRevision;
        private bool _contractRouteRevisionExhausted;
        internal ContractRouteView CaptureContractRouteForDiagnostics() => Volatile.Read(ref _contractRouteView);

        internal void ExhaustContractRouteRevision()
        {
            lock (_bindingGate)
            {
                Volatile.Write(ref _contractRouteRevisionExhausted, true);
                Volatile.Write(ref _boundTargets, []);
                Volatile.Write(ref _contractRouteView, ContractRouteView.Empty);
                _bindingSignal.TrySetResult(CapturePublicationFailureLocked());
            }
        }

        internal void ClearContractRouteForStop()
        {
            lock (_bindingGate)
            {
                Volatile.Write(ref _boundTargets, []);
                Volatile.Write(ref _contractRouteView, ContractRouteView.Empty);
                _bindingSignal.TrySetResult(CapturePublicationFailureLocked());
            }
        }

        internal void ReconcileContractRoute()
        {
            _owner.ContractRouteBuildForTesting?.Invoke(this, "before-reconcile");
            lock (_bindingGate)
            {
                if (_demandRetired || LocalControl.IsRetired || Volatile.Read(ref _owner._contractRoutesStopped)) return;
                if (_owner._cluster is null) return;
                try
                {
                    var topology = LatestContractTopologyLocked();
                    var targets = _boundTargets;
                    var stale = false;
                    foreach (var target in targets)
                        if (!target.IsEligible) { stale = true; break; }
                    if (ReferenceEquals(topology, _contractRouteView.Topology) && !stale) return;
                    var revision = _bindingRevision;
                    if (stale)
                    {
                        if (revision == ulong.MaxValue)
                        {
                            ExhaustContractRouteRevision();
                            throw RouteRevisionExhausted();
                        }
                        _owner.ContractRouteBuildForTesting?.Invoke(this, "targets");
                        targets = targets.Where(static target => target.IsEligible).ToArray();
                        revision++;
                    }
                    var view = BuildContractRouteLocked(topology, targets, revision);
                    _owner.ContractRouteBuildForTesting?.Invoke(this, "signal");
                    var nextSignal = NewBindingSignal();
                    CommitContractRouteLocked(view, nextSignal);
                }
                catch (Exception error)
                {
                    // Carry the original failure in this notification; do not wake a waiter
                    // with an already-completed success task which can spin on an empty view.
                    _bindingSignal.TrySetResult(error);
                    throw;
                }
            }
        }

        private ContractRouteTopology LatestContractTopologyLocked()
        {
            var global = Volatile.Read(ref _owner._contractRouteTopology);
            var current = _contractRouteView.Topology;
            if (_contractRouteRevisionExhausted || global.IsTerminal || current.IsTerminal) throw RouteRevisionExhausted();
            return global.Revision >= current.Revision ? global : current;
        }

        private ContractRouteView BuildContractRouteLocked(
            ContractRouteTopology topology, BoundTarget[] targets, ulong revision)
        {
            if (targets.Length > SharpLinkConnectionPoolOptions.MaximumConnections)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "Contract route target capacity exceeded.");
            _owner.ContractRouteBuildForTesting?.Invoke(this, "buckets");
            var buckets = new ContractRouteBucket[topology.Endpoints.Length];
            var candidates = new SharpLinkEndpointCandidate[buckets.Length];
            for (var index = 0; index < buckets.Length; index++)
            {
                var endpoint = topology.Endpoints[index];
                // Count immutable physical membership, not racing live eligibility, in both
                // passes. Live cuts are checked by the bucket provider/reservation. An exact
                // array avoids a temporary List backing array and cannot leave null holes.
                var count = 0;
                foreach (var target in targets)
                    if (Array.IndexOf(endpoint.Connections, target.Connection) >= 0) count++;
                var selected = new BoundTarget[count];
                var offset = 0;
                foreach (var target in targets)
                    if (Array.IndexOf(endpoint.Connections, target.Connection) >= 0) selected[offset++] = target;
                var bucket = new ContractRouteBucket(endpoint, selected);
                buckets[index] = bucket;
                _owner.ContractRouteBuildForTesting?.Invoke(this, "providers");
                candidates[index] = new SharpLinkEndpointCandidate(endpoint.Endpoint, bucket.EligibleCount,
                    endpoint.ActiveCalls, endpoint.Generation);
            }
            _owner.ContractRouteBuildForTesting?.Invoke(this, "state");
            return new ContractRouteView(topology, revision, targets, buckets, candidates);
        }

        private void CommitContractRouteLocked(ContractRouteView view, TaskCompletionSource<Exception?> nextSignal)
        {
            _bindingRevision = view.BindingRevision;
            Volatile.Write(ref _boundTargets, view.Targets);
            Volatile.Write(ref _contractRouteView, view);
            var oldSignal = _bindingSignal;
            _bindingSignal = nextSignal;
            oldSignal.TrySetResult(CapturePublicationFailureLocked());
        }
    }
}
