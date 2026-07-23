using System.Reflection;

namespace SharpLink.Abstractions;

/// <summary>
/// Coordinates isolated SharpLink clients and routes a contract once while its proxy is created.
/// Subsequent RPC calls execute directly through the selected child client.
/// </summary>
public interface ISharpLinkMultiClusterClient : IAsyncDisposable
{
    /// <summary>Gets the aggregate coordinator lifecycle state.</summary>
    SharpLinkMultiClusterState State { get; }

    /// <summary>Connects every required cluster slot.</summary>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops every cluster slot and releases coordinator-owned state.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a proxy by looking up the contract's cluster route exactly once.</summary>
    TContract Get<TContract>() where TContract : IService;

    /// <summary>Gets the lifecycle state of one configured cluster slot.</summary>
    SharpLinkConnectionState GetClusterState(SharpLinkClusterKey cluster);

    /// <summary>Runs a health check only against the specified cluster slot.</summary>
    ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
        SharpLinkClusterKey cluster,
        CancellationToken cancellationToken = default);

    /// <summary>Registers an assembly's generated artifacts in the specified cluster slot.</summary>
    SharpLinkAssemblyRegistrationResult RegisterAssembly(SharpLinkClusterKey cluster, Assembly assembly);

    /// <summary>Drains and unregisters an assembly from its specified cluster slot.</summary>
    ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
        SharpLinkClusterKey cluster,
        Assembly assembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces an assembly registration inside its specified cluster slot.</summary>
    ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
        SharpLinkClusterKey cluster,
        Assembly oldAssembly,
        Assembly newAssembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default);
}
