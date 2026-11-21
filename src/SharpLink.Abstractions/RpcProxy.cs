namespace SharpLink.Abstractions;

/// <summary>Options captured when a local RPC proxy is created.</summary>
public sealed class RpcProxyOptions
{
    /// <summary>Gets metadata to attach to calls made by this proxy.</summary>
    public SharpLinkMetadata? Metadata { get; init; }
}

/// <summary>A generated service and the explicit preparation for its own calls.</summary>
public sealed class RpcProxy<TContract> where TContract : IService
{
    private readonly IRpcContractChannel _channel;

    internal RpcProxy(TContract service, IRpcContractChannel channel)
    {
        Service = service;
        _channel = channel;
    }

    /// <summary>Gets the service that uses this proxy's prepared route.</summary>
    public TContract Service { get; }

    /// <summary>Prepares this proxy on the current ready connection within a finite timeout.</summary>
    /// <param name="timeout">The maximum time to wait for preparation.</param>
    /// <param name="cancellationToken">Cancels this caller's preparation wait.</param>
    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _channel.PrepareAsync(timeout, cancellationToken);
}
