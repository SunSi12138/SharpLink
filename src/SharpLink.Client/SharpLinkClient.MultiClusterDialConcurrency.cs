namespace SharpLink.Client;

/// <summary>
/// Bounds physical child transport connection attempts shared by one multi-cluster coordinator.
/// The limiter deliberately has coordinator lifetime and is not disposed independently: child
/// shutdown cancels waiters through their own attempt tokens, while in-flight owners release in
/// the transport-dial finally path.
/// </summary>
internal sealed class ClusterDialConcurrencyLimiter
{
    private readonly SemaphoreSlim _permits;

    internal ClusterDialConcurrencyLimiter(int maxConcurrentDials)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentDials, 1);
        _permits = new SemaphoreSlim(maxConcurrentDials, maxConcurrentDials);
    }

    internal Task WaitAsync(CancellationToken cancellationToken)
        => _permits.WaitAsync(cancellationToken);

    internal void Release() => _permits.Release();
}

internal sealed partial class SharpLinkClient
{
    private ClusterDialConcurrencyLimiter? _multiClusterDialLimiter;

    /// <summary>
    /// Binds the coordinator-owned limiter before this child starts any connectivity work.
    /// Rebinding to the same owner is idempotent; rebinding to a different coordinator is invalid.
    /// </summary>
    internal void BindMultiClusterDialLimiter(ClusterDialConcurrencyLimiter limiter)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        var existing = Interlocked.CompareExchange(ref _multiClusterDialLimiter, limiter, null);
        if (existing is not null && !ReferenceEquals(existing, limiter))
        {
            throw new InvalidOperationException(
                "A SharpLink child client cannot be bound to more than one multi-cluster dial limiter.");
        }
    }

    /// <summary>
    /// Executes exactly one physical transport connection attempt under the optional coordinator
    /// permit. Waiting does not own a permit; once acquired, success, failure and cancellation all
    /// release exactly once. Ordinary RPC calls never pass through this boundary.
    /// </summary>
    private async ValueTask<ITransportConnection> ConnectTransportAsync(
        IClientTransportFactory factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var limiter = Volatile.Read(ref _multiClusterDialLimiter);
        if (limiter is null)
            return await factory.ConnectAsync(cancellationToken).ConfigureAwait(false);

        await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await factory.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            limiter.Release();
        }
    }
}
