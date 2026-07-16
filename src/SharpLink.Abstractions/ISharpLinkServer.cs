namespace SharpLink.Abstractions;

/// <summary>Owns a SharpLink listener and all sessions accepted from it.</summary>
public interface ISharpLinkServer : IAsyncDisposable
{
    /// <summary>Gets the current process readiness state.</summary>
    SharpLinkHealthStatus HealthStatus { get; }

    /// <summary>Runs the accept loop until stopped, canceled, or faulted.</summary>
    /// <param name="cancellationToken">Requests immediate shutdown when canceled.</param>
    ValueTask RunAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops accepting, sends GoAway, and drains active calls within the grace period.</summary>
    /// <param name="gracefulTimeout">Maximum time to wait for active calls before cancellation.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared stop operation.</param>
    ValueTask StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);
}
