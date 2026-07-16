namespace SharpLink.Abstractions;

/// <summary>Owns SharpLink client connections and generated contract proxies.</summary>
public interface ISharpLinkClient : IAsyncDisposable
{
    /// <summary>Gets the current atomic client lifecycle state.</summary>
    SharpLinkConnectionState State { get; }

    /// <summary>Connects and completes only after the RPC handshake succeeds.</summary>
    /// <param name="cancellationToken">Cancels the shared connection attempt.</param>
    /// <exception cref="SharpLinkException">The transport or handshake failed.</exception>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops reconnecting, fails pending work, and releases all owned resources.</summary>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared stop operation.</param>
    ValueTask StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the generated proxy for a registered RPC contract.</summary>
    /// <typeparam name="TContract">The generated RPC contract interface.</typeparam>
    TContract Get<TContract>() where TContract : IService;
}
