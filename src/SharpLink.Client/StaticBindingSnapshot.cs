namespace SharpLink.Client;

// Published by the existing pool/topology writer before readiness is signalled. Each
// endpoint retains its generation, immutable connection membership and expansion owner.
internal sealed record BindingEndpointTopology(
    SharpLinkEndpointCandidate? Candidate,
    ClientConnection[] Connections,
    Action EnsureExpansion,
    Action? ReleaseStaleAdmission = null);

internal sealed class BindingTopologySnapshot(BindingEndpointTopology[] endpoints)
{
    internal static readonly BindingTopologySnapshot Empty = new([]);
    internal BindingEndpointTopology[] Endpoints { get; } = endpoints;
    internal ClientConnection[] Connections { get; } = endpoints.SelectMany(endpoint => endpoint.Connections).ToArray();
}

// One atomic publication owns candidates, exclusions and the Session/token pairs they
// index. Selection never consults a contract/manifest map or a newer topology.
internal sealed class StaticBindingSnapshot
{
    internal static readonly StaticBindingSnapshot Empty = Create(BindingTopologySnapshot.Empty, []);
    internal BindingTopologySnapshot Topology { get; }
    internal StaticBoundTarget[] Targets { get; }
    internal StaticBoundEndpoint[] Endpoints { get; }
    internal SharpLinkEndpointCandidate[] Candidates { get; }

    private StaticBindingSnapshot(BindingTopologySnapshot topology, StaticBoundTarget[] targets,
        StaticBoundEndpoint[] endpoints)
    {
        Topology = topology;
        Targets = targets;
        Endpoints = endpoints;
        Candidates = endpoints.Select(endpoint => endpoint.Candidate.GetValueOrDefault()).ToArray();
    }

    internal static StaticBindingSnapshot Create(BindingTopologySnapshot topology, StaticBoundTarget[] targets)
    {
        var endpoints = new List<StaticBoundEndpoint>();
        foreach (var endpoint in topology.Endpoints)
        {
            var bound = targets.Where(target => Array.IndexOf(endpoint.Connections, target.Connection) >= 0).ToArray();
            if (bound.Length != 0) endpoints.Add(new StaticBoundEndpoint(endpoint, bound));
        }
        return new(topology, targets, endpoints.ToArray());
    }

    internal StaticBindingSnapshot Filter(Func<StaticBoundTarget, bool> predicate)
    {
        var retained = Targets.Where(predicate).ToArray();
        return retained.Length == Targets.Length ? this : Create(Topology, retained);
    }
}

internal sealed class StaticBoundEndpoint
{
    internal BindingEndpointTopology Topology { get; }
    internal StaticBoundTarget[] Targets { get; }
    internal SharpLinkEndpointCandidate? Candidate { get; }

    internal StaticBoundEndpoint(BindingEndpointTopology topology, StaticBoundTarget[] targets)
    {
        Topology = topology;
        Targets = targets;
        if (topology.Candidate is { } candidate)
            Candidate = new SharpLinkEndpointCandidate(candidate.Endpoint,
                GetReadyCount,
                () => candidate.ActiveCallCount, candidate.Generation);
    }

    private int GetReadyCount()
    {
        var count = 0;
        foreach (var target in Targets)
            if (target.Connection.CanAcceptCalls) count++;
        return count;
    }

    internal bool TryReserve(out StaticBoundTarget selected)
    {
        var first = 0;
        if (Targets.Length > 1)
        {
            first = Random.Shared.Next(Targets.Length);
            var second = Random.Shared.Next(Targets.Length - 1);
            if (second >= first) second++;
            if (Targets[second].Connection.ActiveCallCount < Targets[first].Connection.ActiveCallCount) first = second;
        }
        if (TryReserve(first, out selected)) return true;
        for (var index = 0; index < Targets.Length; index++)
            if (index != first && TryReserve(index, out selected)) return true;
        selected = default;
        return false;
    }

    private bool TryReserve(int index, out StaticBoundTarget selected)
    {
        selected = Targets[index];
        if (!selected.Connection.TryReserveCallAdmission(out var admitted)) return false;
        if (ReferenceEquals(admitted, selected.Connection)) return true;
        // A blue/green admission redirect cannot borrow its predecessor's token. A
        // separately confirmed replacement, if present, gets its own reservation below.
        admitted.ReleaseCallAdmissionReservation();
        return false;
    }
}
