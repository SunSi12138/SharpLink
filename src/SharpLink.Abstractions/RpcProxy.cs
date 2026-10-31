namespace SharpLink.Abstractions;

/// <summary>A generated service and preparation handle paired with the same exact contract channel.</summary>
/// <typeparam name="TContract">The generated RPC contract interface.</typeparam>
/// <remarks>The service invokes its channel directly; this handle adds no per-invocation forwarding layer.</remarks>
public sealed class RpcProxy<TContract> where TContract : IService
{
    private readonly IRpcContractChannel _channel;

    internal RpcProxy(TContract service, IRpcContractChannel channel)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(channel);
        Service = service;
        _channel = channel;
    }

    /// <summary>Gets the generated service bound at construction to this handle's exact channel.</summary>
    public TContract Service { get; }

    /// <summary>Waits within one finite budget for the original channel's exact binding requirement.</summary>
    /// <param name="timeout">A positive finite timeout no greater than 2,147,483,647 milliseconds.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait.</param>
    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _channel.PrepareAsync(timeout, cancellationToken);
}
