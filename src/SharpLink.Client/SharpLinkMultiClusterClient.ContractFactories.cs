using System.Reflection;

namespace SharpLink.Client;

/// <summary>Internal child seam that defers demand publication until the captured coordinator route validates.</summary>
internal interface ISharpLinkContractFactoryTransaction
{
    RpcProxy<TContract> CreateProxyTransactional<TContract>(RpcProxyOptions? options, Action validatePublication)
        where TContract : IService;
    IRpcContractChannel CreateContractChannelTransactional(
        RpcLocalContract local, RpcProxyOptions? options, Action validatePublication);
}

internal sealed partial class SharpLinkMultiClusterClient
{
    public RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null) where TContract : IService
    {
        options?.Validate();
        SharpLinkClusterRouteRegistration route;
        lock (_gate)
        {
            ValidateContractFactoryStateLocked();
            if (!_snapshot.Routes.TryGetValue(typeof(TContract), out route!))
                throw ClientLocalContractControl.Unavailable();
        }
        var frozenOptions = CaptureContractOptions(route.Slot.Key, options);
        return GetContractFactoryTransaction(route.Slot).CreateProxyTransactional<TContract>(
            frozenOptions, () => ValidateContractFactoryPublication(route.Slot, route));
    }

    public RpcLocalContract GetLocalContract(SharpLinkClusterKey cluster, Assembly registeredAssembly, long contractId)
    {
        ValidateClusterKey(cluster);
        ArgumentNullException.ThrowIfNull(registeredAssembly);
        if (contractId == 0)
            throw new ArgumentOutOfRangeException(nameof(contractId));
        var slot = CaptureContractFactorySlot(cluster);
        var local = slot.Client.GetLocalContract(registeredAssembly, contractId);
        ValidateContractFactoryPublication(slot, null);
        return local;
    }

    public IRpcContractChannel CreateContractChannel(
        SharpLinkClusterKey cluster, RpcLocalContract local, RpcProxyOptions? options = null)
    {
        ValidateClusterKey(cluster);
        ArgumentNullException.ThrowIfNull(local);
        options?.ValidateFor(local);
        var frozenOptions = CaptureContractOptions(cluster, options);
        var slot = CaptureContractFactorySlot(cluster);
        return GetContractFactoryTransaction(slot).CreateContractChannelTransactional(
            local, frozenOptions, () => ValidateContractFactoryPublication(slot, null));
    }

    private SharpLinkClusterSlot CaptureContractFactorySlot(SharpLinkClusterKey cluster)
    {
        lock (_gate)
        {
            ValidateContractFactoryStateLocked();
            return GetSlot(cluster);
        }
    }

    private void ValidateContractFactoryPublication(
        SharpLinkClusterSlot capturedSlot, SharpLinkClusterRouteRegistration? capturedRoute)
    {
        lock (_gate)
        {
            ValidateContractFactoryStateLocked();
            if (!_snapshot.Clusters.TryGetValue(capturedSlot.Key, out var currentSlot) ||
                !ReferenceEquals(currentSlot, capturedSlot) ||
                (capturedRoute is not null &&
                 (!_snapshot.Routes.TryGetValue(capturedRoute.ContractType, out var currentRoute) ||
                  !ReferenceEquals(currentRoute, capturedRoute))))
                throw ClientLocalContractControl.Unavailable();
            // Exact slot/route objects are immutable generation identities. Replacement after this
            // point follows successful publication; the returned handle stays on its original site.
        }
    }

    private void ValidateContractFactoryStateLocked()
    {
        var state = (SharpLinkMultiClusterState)_state;
        if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
            throw new InvalidOperationException($"Multi-cluster client state '{state}' does not create contract handles.");
    }

    private static RpcProxyOptions CaptureContractOptions(SharpLinkClusterKey cluster, RpcProxyOptions? options)
    {
        if (options?.TargetScope is { } requested && requested != cluster)
            throw new ArgumentException("The requested scope differs from the captured cluster route.", nameof(options));
        return new RpcProxyOptions
        {
            TargetScope = cluster,
            Requirement = options?.Requirement ?? RpcBindingRequirement.Compatible,
            Receipt = options?.Receipt,
            Metadata = options?.Metadata
        };
    }

    private static ISharpLinkContractFactoryTransaction GetContractFactoryTransaction(SharpLinkClusterSlot slot)
        => slot.Client as ISharpLinkContractFactoryTransaction ?? throw new NotSupportedException(
            "An injected cluster client must implement transactional contract factory publication.");
}
