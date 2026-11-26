namespace SharpLink.Abstractions;

/// <summary>Options captured when a local RPC proxy is created.</summary>
public sealed class RpcProxyOptions
{
    /// <summary>Gets metadata to attach to calls made by this proxy.</summary>
    public SharpLinkMetadata? Metadata { get; init; }

    /// <summary>Prepares the current Ready Sessions of the client and balances calls only among them.</summary>
    /// <remarks>New Sessions remain ineligible until another explicit Prepare. The default pins one Session.</remarks>
    public bool BalancePreparedEndpoints { get; init; }
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

    /// <summary>Prepares this proxy's selected Ready Session or endpoint cohort within a finite timeout.</summary>
    /// <remarks>Each physical session has a bounded binding table. Unloading a local contract alone
    /// does not close its remote binding; the session can retain it for coordinated redeployment.</remarks>
    /// <param name="timeout">The maximum time to wait for preparation.</param>
    /// <param name="cancellationToken">Cancels this caller's preparation wait.</param>
    /// <exception cref="SharpLinkException">A full binding table returns
    /// <see cref="SharpLinkErrorCode.ResourceExhausted"/> with
    /// <see cref="SharpLinkErrorDetails.ResourceExhausted.PreparedBindingCapacity"/>.</exception>
    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _channel.PrepareAsync(timeout, cancellationToken);
}
