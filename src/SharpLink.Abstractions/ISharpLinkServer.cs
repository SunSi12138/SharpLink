namespace SharpLink.Abstractions;

/// <summary>Owns a SharpLink listener, its local runtime, and all sessions accepted from it.</summary>
public interface ISharpLinkServer : IAsyncDisposable
{
    /// <summary>Gets the lifecycle state of the local server runtime.</summary>
    SharpLinkServerLifecycleState LifecycleState { get; }

    /// <summary>Gets local RPC readiness independently of the lifecycle state.</summary>
    SharpLinkHealthStatus HealthStatus { get; }

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
    /// <param name="oldAssembly">The exact Assembly object used by the running registration.</param>
    /// <param name="newAssembly">The assembly whose validated generated artifacts replace the old routes.</param>
    /// <param name="gracefulTimeout">Maximum time to wait before canceling calls owned by the old registration.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait; publication, draining, and cleanup continue.</param>
    /// <returns>The transactional publication result and the bounded old-registration drain state.</returns>
    ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
        System.Reflection.Assembly oldAssembly,
        System.Reflection.Assembly newAssembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the local serving runtime and completes after the accept infrastructure is active.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels startup before <see cref="SharpLinkServerLifecycleState.Running"/> is published.
    /// It does not become a server lifetime token after startup succeeds.
    /// </param>
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for an independently triggered shutdown to finish without initiating shutdown.
    /// </summary>
    /// <remarks>
    /// Normal shutdown completes successfully. An unrecoverable runtime or shutdown failure is propagated only
    /// after the Server-owned terminal cleanup has completed; callers can also inspect <see cref="LifecycleState"/>.
    /// </remarks>
    /// <param name="cancellationToken">Cancels only this caller's wait.</param>
    Task WaitForShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Initiates or joins the shared Server-owned shutdown operation, drains active work, and releases runtime resources.
    /// </summary>
    /// <remarks>
    /// Caller cancellation stops only that caller from waiting. The shared shutdown continues independently.
    /// If terminal cleanup leaves the Server <see cref="SharpLinkServerLifecycleState.Faulted"/>, this operation and
    /// <see cref="WaitForShutdownAsync(CancellationToken)"/> expose the same terminal failure.
    /// </remarks>
    /// <param name="gracefulTimeout">Maximum time to wait for active calls before cancellation.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared stop operation.</param>
    ValueTask StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);
}
