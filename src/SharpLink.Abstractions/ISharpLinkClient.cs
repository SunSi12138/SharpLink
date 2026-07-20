namespace SharpLink.Abstractions;

/// <summary>Owns SharpLink client connections and generated contract proxies.</summary>
public interface ISharpLinkClient : IAsyncDisposable
{
    /// <summary>Gets the current atomic client lifecycle state.</summary>
    SharpLinkConnectionState State { get; }

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

    /// <summary>Connects and completes only after the RPC handshake succeeds.</summary>
    /// <param name="cancellationToken">Cancels the shared connection attempt.</param>
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
}
