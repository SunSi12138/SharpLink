namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    public TContract CreateProxy<TContract>(RpcProxyOptions? options = null) where TContract : IService
    {
        options ??= new RpcProxyOptions();
        if (options is { Availability: RpcAvailability.FailFast, Requirement: null })
            return Get<TContract>();
        var state = State;
        if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
            throw new InvalidOperationException($"Multi-cluster client state '{state}' does not create proxies.");
        if (Volatile.Read(ref _snapshot).Routes.TryGetValue(typeof(TContract), out var route))
            return route.Slot.Client.CreateProxy<TContract>(options);
        throw new InvalidOperationException($"Proxy for service interface {typeof(TContract).FullName} is not routed to a cluster.");
    }

    public ContractReadinessSnapshot GetContractReadiness(
        SharpLinkClusterKey cluster,
        ContractRequirement requirement)
        => GetSlot(cluster).Client.GetContractReadiness(requirement);

    public ValueTask<ContractReadinessSnapshot> WaitForContractAsync(
        SharpLinkClusterKey cluster,
        ContractRequirement requirement,
        ContractWaitOptions? options = null,
        CancellationToken cancellationToken = default)
        // Capture one slot lifetime. Removal stops its child; a newly added slot with the same
        // key must not silently become the target of this existing observation wait.
        => GetSlot(cluster).Client.WaitForContractAsync(requirement, options, cancellationToken);
}
