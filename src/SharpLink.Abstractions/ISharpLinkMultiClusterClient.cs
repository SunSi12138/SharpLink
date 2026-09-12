using System.Reflection;

namespace SharpLink.Abstractions;

/// <summary>
/// Coordinates isolated SharpLink clients and routes a contract once while its proxy is created.
/// Subsequent RPC calls execute directly through the selected child client.
/// </summary>
public interface ISharpLinkMultiClusterClient : IAsyncDisposable
{
    /// <summary>
    /// Gets the legacy aggregate connection/readiness projection.
    /// Use <see cref="LifecycleState"/> and <see cref="Readiness"/> for unambiguous state domains.
    /// </summary>
    SharpLinkMultiClusterState State { get; }

    /// <summary>Gets the lifecycle of the local multi-cluster coordinator.</summary>
    /// <remarks>Legacy custom implementations receive a projection of <see cref="State"/> by default.</remarks>
    SharpLinkClientLifecycleState LifecycleState => State switch
    {
        SharpLinkMultiClusterState.Created => SharpLinkClientLifecycleState.Created,
        SharpLinkMultiClusterState.Connecting => SharpLinkClientLifecycleState.Starting,
        SharpLinkMultiClusterState.Ready => SharpLinkClientLifecycleState.Running,
        SharpLinkMultiClusterState.Degraded => SharpLinkClientLifecycleState.Running,
        SharpLinkMultiClusterState.Draining => SharpLinkClientLifecycleState.Draining,
        SharpLinkMultiClusterState.Stopped => SharpLinkClientLifecycleState.Stopped,
        SharpLinkMultiClusterState.Faulted => SharpLinkClientLifecycleState.Faulted,
        _ => SharpLinkClientLifecycleState.Faulted
    };

    /// <summary>Gets aggregate readiness across the currently configured cluster slots.</summary>
    SharpLinkReadinessState Readiness => State switch
    {
        SharpLinkMultiClusterState.Ready => SharpLinkReadinessState.Ready,
        SharpLinkMultiClusterState.Degraded => SharpLinkReadinessState.Degraded,
        _ => SharpLinkReadinessState.NotReady
    };

    /// <summary>Starts the local coordinator and child runtimes without waiting for remote readiness.</summary>
    /// <remarks>
    /// Built-in SharpLink coordinators provide non-blocking local startup. Legacy custom implementations fall back
    /// to <see cref="ConnectAsync(CancellationToken)"/> until they override this member.
    /// </remarks>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared start operation.</param>
    ValueTask StartAsync(CancellationToken cancellationToken = default)
        => ConnectAsync(cancellationToken);

    /// <summary>
    /// Runs or joins the legacy all-required-slots connection operation.
    /// This compatibility API does not define <see cref="LifecycleState"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared connection operation.</param>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits until every currently configured cluster slot is ready.</summary>
    /// <param name="cancellationToken">Cancels only this caller's readiness wait.</param>
    ValueTask WaitForReadyAsync(CancellationToken cancellationToken = default)
        => ConnectAsync(cancellationToken);

    /// <summary>Waits until one specified cluster slot is ready.</summary>
    /// <remarks>
    /// Legacy custom implementations without a scoped readiness primitive conservatively wait on the aggregate
    /// connection operation when the requested slot is not already ready.
    /// </remarks>
    /// <param name="cluster">The configured cluster to observe.</param>
    /// <param name="cancellationToken">Cancels only this caller's readiness wait.</param>
    ValueTask WaitForReadyAsync(
        SharpLinkClusterKey cluster,
        CancellationToken cancellationToken = default)
        => GetClusterState(cluster) == SharpLinkConnectionState.Ready
            ? ValueTask.CompletedTask
            : ConnectAsync(cancellationToken);

    /// <summary>Waits for independently requested coordinator shutdown without initiating it.</summary>
    /// <remarks>
    /// Legacy custom implementations must override this member to expose a termination signal while running.
    /// </remarks>
    /// <param name="cancellationToken">Cancels only this caller's wait.</param>
    Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
        => State == SharpLinkMultiClusterState.Stopped
            ? Task.CompletedTask
            : Task.FromException(new NotSupportedException(
                "This custom multi-cluster client does not expose a shutdown completion signal."));

    /// <summary>Stops every cluster slot and releases coordinator-owned state.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a proxy by looking up the contract's cluster route exactly once.</summary>
    TContract Get<TContract>() where TContract : IService;

    /// <summary>Creates a routed proxy that attaches one immutable metadata snapshot to every invocation.</summary>
    TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService;

    /// <summary>Attempts to capture one point-in-time status snapshot for a configured cluster slot.</summary>
    /// <remarks>
    /// A valid cluster key that is not currently configured, including one concurrently removed before the lookup,
    /// returns <see langword="false"/> instead of using an exception as normal control flow. Invalid or default keys
    /// remain programmer errors and throw <see cref="ArgumentException"/>. The built-in coordinator overrides this
    /// compatibility implementation so the cluster lookup and child-state read use one immutable coordinator snapshot.
    /// </remarks>
    /// <param name="cluster">The cluster key to query.</param>
    /// <param name="status">Receives the status snapshot when the cluster is present; otherwise the default value.</param>
    /// <returns><see langword="true"/> when the cluster is currently configured; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="cluster"/> is the default or otherwise invalid.</exception>
    bool TryGetClusterStatus(SharpLinkClusterKey cluster, out SharpLinkClusterStatusSnapshot status)
    {
        if (!SharpLinkClusterKey.IsValid(cluster.Value))
        {
            throw new ArgumentException(
                "A valid non-default SharpLinkClusterKey is required.",
                nameof(cluster));
        }

        try
        {
            status = new SharpLinkClusterStatusSnapshot(cluster, GetClusterState(cluster));
            return true;
        }
        catch (ArgumentException exception) when (exception.ParamName == nameof(cluster))
        {
            status = default;
            return false;
        }
    }

    /// <summary>Gets the legacy connection-oriented state of one configured cluster slot.</summary>
    SharpLinkConnectionState GetClusterState(SharpLinkClusterKey cluster);

    /// <summary>Gets the connectivity state of one configured cluster runtime.</summary>
    SharpLinkClusterState GetClusterRuntimeState(SharpLinkClusterKey cluster)
        => GetClusterState(cluster) switch
        {
            SharpLinkConnectionState.Created => SharpLinkClusterState.Inactive,
            SharpLinkConnectionState.Connecting => SharpLinkClusterState.Connecting,
            SharpLinkConnectionState.Ready => SharpLinkClusterState.Ready,
            SharpLinkConnectionState.Draining => SharpLinkClusterState.Draining,
            SharpLinkConnectionState.Reconnecting => SharpLinkClusterState.Reconnecting,
            SharpLinkConnectionState.Stopped => SharpLinkClusterState.Stopped,
            SharpLinkConnectionState.Faulted => SharpLinkClusterState.Unavailable,
            _ => SharpLinkClusterState.Unavailable
        };

    /// <summary>Gets the readiness of one configured cluster slot.</summary>
    SharpLinkReadinessState GetClusterReadiness(SharpLinkClusterKey cluster)
        => GetClusterState(cluster) == SharpLinkConnectionState.Ready
            ? SharpLinkReadinessState.Ready
            : SharpLinkReadinessState.NotReady;

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
