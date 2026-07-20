namespace SharpLink.Abstractions;

/// <summary>Owns a SharpLink listener and all sessions accepted from it.</summary>
public interface ISharpLinkServer : IAsyncDisposable
{
    /// <summary>Gets the current process readiness state.</summary>
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

    /// <summary>Runs the accept loop until stopped, canceled, or faulted.</summary>
    /// <param name="cancellationToken">Requests immediate shutdown when canceled.</param>
    ValueTask RunAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops accepting, sends GoAway, and drains active calls within the grace period.</summary>
    /// <param name="gracefulTimeout">Maximum time to wait for active calls before cancellation.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared stop operation.</param>
    ValueTask StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);
}
