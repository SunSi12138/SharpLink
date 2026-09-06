namespace SharpLink.Abstractions;

/// <summary>Owns a SharpLink listener and all sessions accepted from it.</summary>
public interface ISharpLinkServer : ISharpLinkAssemblyRegistry, IAsyncDisposable
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

    /// <summary>Runs the accept loop until stopped, canceled, or faulted.</summary>
    /// <param name="cancellationToken">Requests immediate shutdown when canceled.</param>
    ValueTask RunAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops accepting, sends GoAway, and drains active calls within the grace period.</summary>
    /// <param name="gracefulTimeout">Maximum time to wait for active calls before cancellation.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared stop operation.</param>
    ValueTask StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);
}
