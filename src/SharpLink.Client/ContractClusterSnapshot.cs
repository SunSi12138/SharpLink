namespace SharpLink.Client;

/// <summary>One site's qualified topology, candidates and retry-index space are published together.</summary>
internal sealed class ContractClusterSnapshot
{
    internal ContractClusterSnapshot(object sourceTopology, ContractEndpointTargets[] endpoints)
    { SourceTopology = sourceTopology; Endpoints = endpoints; Candidates = endpoints.Select(static e => e.Candidate).ToArray(); }
    internal object SourceTopology { get; }
    internal ContractEndpointTargets[] Endpoints { get; }
    internal SharpLinkEndpointCandidate[] Candidates { get; }

    internal static ContractClusterSnapshot Create(object sourceTopology, SharpLinkEndpointCandidate[] candidates,
        ClientConnection[][] connections, object[] endpointOwners, BoundContractTarget[] targets)
    {
        var byConnection = targets.ToDictionary(static t => t.Connection);
        var qualified = new List<ContractEndpointTargets>(candidates.Length);
        for (var index = 0; index < candidates.Length; index++)
        {
            var members = new List<BoundContractTarget>(connections[index].Length);
            foreach (var connection in connections[index])
                if (byConnection.TryGetValue(connection, out var target) && connection.CanAcceptCalls) members.Add(target);
            if (members.Count != 0) qualified.Add(new(endpointOwners[index], candidates[index], [.. members]));
        }
        return new(sourceTopology, [.. qualified]);
    }
}

internal sealed class ContractEndpointTargets
{
    internal ContractEndpointTargets(object sourceOwner, SharpLinkEndpointCandidate source, BoundContractTarget[] targets)
    {
        SourceOwner = sourceOwner; Targets = targets; Connections = targets.Select(static t => t.Connection).ToArray();
        Candidate = new(source.Endpoint, () => Connections.Length, GetActiveCallCount, source.Generation);
    }
    internal object SourceOwner { get; }
    internal BoundContractTarget[] Targets { get; }
    internal ClientConnection[] Connections { get; }
    internal SharpLinkEndpointCandidate Candidate { get; }
    private int GetActiveCallCount()
    { var count = 0; foreach (var connection in Connections) count += connection.ActiveCallCount; return count; }
    internal BoundContractTarget? TryReserve(ContractSiteKey key)
    {
        var admitted = EndpointSelectionKernel.SelectConnection(Connections);
        if (admitted is null) return null;
        foreach (var target in Targets)
            if (ReferenceEquals(target.Connection, admitted) && target.SessionGeneration == admitted.BindingGeneration && admitted.Bindings.IsConfirmed(key, target.Token))
                return target;
        admitted.ReleaseCallAdmissionReservation();
        return null;
    }
}
