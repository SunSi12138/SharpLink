namespace SharpLink.Abstractions;

/// <summary>Owns SharpLink client connections and generated contract proxies.</summary>
public interface ISharpLinkClient : ISharpLinkAssemblyRegistry, IAsyncDisposable
{
    /// <summary>Gets the current atomic client lifecycle state.</summary>
    SharpLinkConnectionState State { get; }

    /// <summary>
    /// Gets an immutable point-in-time observation of the active endpoint topology without waiting,
    /// locking, or traversing endpoint collections.
    /// </summary>
    /// <returns>The latest published topology readiness snapshot.</returns>
    /// <exception cref="NotSupportedException">
    /// This implementation does not expose endpoint readiness details.
    /// </exception>
    SharpLinkClientReadinessSnapshot GetReadinessSnapshot()
        => throw new NotSupportedException(
            "This ISharpLinkClient implementation does not expose endpoint readiness details.");

    /// <summary>
    /// Starts or joins the topology's existing connectivity lifecycle when necessary, then waits
    /// until a point-in-time Ready-state observation contains at least the requested number of ready
    /// endpoints. A successful result is not a lease or a guarantee that topology readiness will be
    /// retained after the method returns. The wait does not raise the configured convergence target.
    /// </summary>
    /// <param name="minimumReadyEndpoints">The minimum number of active ready endpoints to observe.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait.</param>
    /// <returns>The snapshot that satisfied the requested threshold.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minimumReadyEndpoints"/> is less than one or exceeds this topology's configured
    /// readiness limit.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// This implementation does not support endpoint readiness waits.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled. The Client-owned connectivity lifecycle continues.
    /// </exception>
    /// <exception cref="SharpLinkException">
    /// The joined initial connectivity attempt failed, the observed attempt entered Faulted, or the
    /// Client began draining or stopped.
    /// </exception>
    ValueTask<SharpLinkClientReadinessSnapshot> WaitForReadinessAsync(
        int minimumReadyEndpoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumReadyEndpoints, 1);
        return ValueTask.FromException<SharpLinkClientReadinessSnapshot>(
            new NotSupportedException(
                "This ISharpLinkClient implementation does not support endpoint readiness waits."));
    }

    /// <summary>
    /// Atomically replaces the client interceptor pipeline for logical RPCs that start after this call returns.
    /// Calls already in progress retain the interceptor generation captured at their invocation boundary.
    /// </summary>
    /// <param name="interceptors">The complete interceptor pipeline in execution order. The sequence is copied before publication.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interceptors"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="interceptors"/> contains a null element.</exception>
    /// <exception cref="InvalidOperationException">The client is draining, stopped, or faulted.</exception>
    /// <exception cref="NotSupportedException">This implementation does not support runtime interceptor replacement.</exception>
    void ReplaceInterceptors(IEnumerable<ISharpLinkClientInterceptor> interceptors)
    {
        ArgumentNullException.ThrowIfNull(interceptors);
        throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime interceptor replacement.");
    }

    /// <summary>
    /// Starts the topology-specific connectivity lifecycle and completes according to its existing
    /// connectivity boundary. This method does not wait for multi-endpoint convergence.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels only this caller's wait; the shared client-owned connection attempt continues.
    /// </param>
    /// <exception cref="SharpLinkException">The transport or handshake failed.</exception>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops reconnecting, fails pending work, and releases all owned resources.</summary>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared stop operation.</param>
    ValueTask StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Queries the selected ready connection using the protocol health control frame.</summary>
    /// <param name="cancellationToken">Cancels the local health request.</param>
    /// <returns>The remote server readiness state.</returns>
    ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Creates the generated proxy for a registered RPC contract.</summary>
    /// <typeparam name="TContract">The generated RPC contract interface.</typeparam>
    TContract Get<TContract>() where TContract : IService;

    /// <summary>Creates a generated proxy that attaches one immutable metadata snapshot to every invocation.</summary>
    /// <typeparam name="TContract">The generated RPC contract interface.</typeparam>
    /// <param name="metadata">Envelope metadata attached without adding a business-contract parameter.</param>
    TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService;
}
