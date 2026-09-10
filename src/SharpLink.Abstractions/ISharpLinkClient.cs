namespace SharpLink.Abstractions;

/// <summary>Owns the SharpLink client runtime, remote connections, and generated contract proxies.</summary>
public interface ISharpLinkClient : IAsyncDisposable
{
    /// <summary>
    /// Gets the legacy connection-oriented state projection.
    /// Use <see cref="LifecycleState"/>, <see cref="Readiness"/>, and <see cref="ClusterState"/>
    /// when lifecycle and remote availability must be distinguished.
    /// </summary>
    SharpLinkConnectionState State { get; }

    /// <summary>Gets the lifecycle of the local client runtime.</summary>
    /// <remarks>Legacy custom implementations receive a projection of <see cref="State"/> by default.</remarks>
    SharpLinkClientLifecycleState LifecycleState => State switch
    {
        SharpLinkConnectionState.Created => SharpLinkClientLifecycleState.Created,
        SharpLinkConnectionState.Connecting => SharpLinkClientLifecycleState.Starting,
        SharpLinkConnectionState.Ready => SharpLinkClientLifecycleState.Running,
        SharpLinkConnectionState.Reconnecting => SharpLinkClientLifecycleState.Running,
        SharpLinkConnectionState.Draining => SharpLinkClientLifecycleState.Draining,
        SharpLinkConnectionState.Stopped => SharpLinkClientLifecycleState.Stopped,
        SharpLinkConnectionState.Faulted => SharpLinkClientLifecycleState.Faulted,
        _ => SharpLinkClientLifecycleState.Faulted
    };

    /// <summary>Gets the current remote-call readiness independently of the local lifecycle.</summary>
    SharpLinkReadinessState Readiness => State == SharpLinkConnectionState.Ready
        ? SharpLinkReadinessState.Ready
        : SharpLinkReadinessState.NotReady;

    /// <summary>Gets the current connectivity state of this client's configured cluster.</summary>
    SharpLinkClusterState ClusterState => State switch
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

    /// <summary>
    /// Starts locally owned client supervisors and returns without waiting for a remote endpoint to become ready.
    /// </summary>
    /// <remarks>
    /// Built-in SharpLink clients provide non-blocking local startup. Legacy custom implementations fall back to
    /// <see cref="ConnectAsync(CancellationToken)"/> until they override this member.
    /// </remarks>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared start operation.</param>
    ValueTask StartAsync(CancellationToken cancellationToken = default)
        => ConnectAsync(cancellationToken);

    /// <summary>
    /// Runs or joins the legacy explicit connection attempt and completes after a usable remote connection exists.
    /// This compatibility API does not define <see cref="LifecycleState"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared connection attempt.</param>
    /// <exception cref="SharpLinkException">The transport or handshake failed.</exception>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits until this client's configured cluster has a usable remote connection.</summary>
    /// <param name="cancellationToken">Cancels only this caller's readiness wait.</param>
    ValueTask WaitForReadyAsync(CancellationToken cancellationToken = default)
        => ConnectAsync(cancellationToken);

    /// <summary>
    /// Waits for an independently requested shutdown to finish without initiating shutdown itself.
    /// </summary>
    /// <remarks>
    /// Legacy custom implementations must override this member to expose a termination signal while running.
    /// </remarks>
    /// <param name="cancellationToken">Cancels only this caller's wait.</param>
    Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
        => State == SharpLinkConnectionState.Stopped
            ? Task.CompletedTask
            : Task.FromException(new NotSupportedException(
                "This custom client does not expose a shutdown completion signal."));

    /// <summary>Stops reconnecting, fails pending work, and releases all owned resources.</summary>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared stop operation.</param>
    ValueTask StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Atomically registers the source-generated artifacts owned by an already loaded assembly.</summary>
    /// <param name="assembly">The assembly containing a generated SharpLink manifest.</param>
    /// <returns>A non-throwing registration result with structured diagnostics after rejection.</returns>
    SharpLinkAssemblyRegistrationResult RegisterAssembly(System.Reflection.Assembly assembly);

    /// <summary>Drains and unregisters one previously registered assembly.</summary>
    /// <param name="assembly">The exact Assembly object used during registration.</param>
    /// <param name="gracefulTimeout">Maximum time to wait before canceling calls owned by the module.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait; draining continues.</param>
    ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
        System.Reflection.Assembly assembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default);

    /// <summary>Prepares a generated assembly and atomically replaces one runtime registration before draining it.</summary>
    /// <param name="oldAssembly">The exact Assembly object used for the running registration.</param>
    /// <param name="newAssembly">The assembly whose validated generated artifacts replace the old routes.</param>
    /// <param name="gracefulTimeout">Maximum time to wait before canceling calls owned by the old registration.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait; publication and cleanup continue.</param>
    /// <returns>The transactional publication result and the bounded old-registration drain state.</returns>
    ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
        System.Reflection.Assembly oldAssembly,
        System.Reflection.Assembly newAssembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default);

    /// <summary>Queries the selected ready connection using the protocol health control frame.</summary>
    /// <param name="cancellationToken">Cancels the local health request.</param>
    /// <returns>The remote server readiness state.</returns>
    ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Creates the generated proxy for a registered RPC contract.</summary>
    /// <typeparam name="TContract">The generated RPC contract interface.</typeparam>
    TContract Get<TContract>() where TContract : IService;
}
