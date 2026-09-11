namespace SharpLink.Abstractions;

/// <summary>Owns a SharpLink listener, its local runtime, and all sessions accepted from it.</summary>
public interface ISharpLinkServer : ISharpLinkAssemblyRegistry, IAsyncDisposable
{
    /// <summary>Gets the lifecycle state of the local server runtime.</summary>
    SharpLinkServerLifecycleState LifecycleState { get; }

    /// <summary>Gets local RPC readiness independently of the lifecycle state.</summary>
    SharpLinkHealthStatus HealthStatus { get; }

    /// <summary>
    /// Atomically replaces the server interceptor pipeline for service invocations that start after this call returns.
    /// Calls already in progress retain the interceptor generation captured at their dispatch boundary.
    /// </summary>
    /// <param name="interceptors">The complete interceptor pipeline in execution order. The sequence is copied before publication.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interceptors"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="interceptors"/> contains a null element.</exception>
    /// <exception cref="InvalidOperationException">The server is draining, stopped, or faulted.</exception>
    /// <exception cref="NotSupportedException">This implementation does not support runtime interceptor replacement.</exception>
    void ReplaceInterceptors(IEnumerable<ISharpLinkServerInterceptor> interceptors)
    {
        ArgumentNullException.ThrowIfNull(interceptors);
        throw new NotSupportedException(
            "This ISharpLinkServer implementation does not support runtime interceptor replacement.");
    }

    /// <summary>
    /// Atomically replaces the server-local Response compression policy. The next Response or
    /// server-to-client StreamData frame captures the new policy at its compression decision point.
    /// </summary>
    /// <param name="policy">The complete replacement policy.</param>
    void UpdateResponseCompressionPolicy(SharpLinkCompressionSendPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        throw new NotSupportedException(
            "This ISharpLinkServer implementation does not support runtime response compression policy updates.");
    }

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
