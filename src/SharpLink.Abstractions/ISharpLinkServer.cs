namespace SharpLink.Abstractions;

/// <summary>Owns a SharpLink listener and all sessions accepted from it.</summary>
public interface ISharpLinkServer : IAsyncDisposable
{
    /// <summary>Gets the current process readiness state.</summary>
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
    /// <param name="cancellationToken">Cancels only this caller's wait; publication, draining, and cleanup continue.</param>
    /// <returns>The transactional publication result and the bounded old-registration drain state.</returns>
    ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
        System.Reflection.Assembly oldAssembly,
        System.Reflection.Assembly newAssembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default);

    /// <summary>Runs the accept loop until stopped, canceled, or faulted.</summary>
    /// <param name="cancellationToken">Requests immediate shutdown when canceled.</param>
    ValueTask RunAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops accepting, sends GoAway, and drains active calls within the grace period.</summary>
    /// <param name="gracefulTimeout">Maximum time to wait for active calls before cancellation.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared stop operation.</param>
    ValueTask StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);
}
