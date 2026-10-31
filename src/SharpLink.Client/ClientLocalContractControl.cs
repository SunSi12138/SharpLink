namespace SharpLink.Client;

/// <summary>Host-owned lifetime gate for one exact local registration; contains no collectible payload.</summary>
internal sealed class ClientLocalContractControl(RpcLocalContract local)
{
    private int _retired;
    private int _retirementNotified;
    private Action<ClientLocalContractControl>? _retirementCallback;

    internal RpcLocalContract Local { get; } = local;
    internal bool IsRetired => Volatile.Read(ref _retired) != 0;

    internal void ThrowIfRetired()
    {
        if (IsRetired)
            throw Unavailable();
    }

    // Closing the gate never calls out while the registry publication lock is held.
    internal void Retire() => Interlocked.Exchange(ref _retired, 1);

    internal void SetRetirementCallback(Action<ClientLocalContractControl> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Interlocked.CompareExchange(ref _retirementCallback, callback, null);
        if (IsRetired)
            NotifyRetired();
    }

    internal void NotifyRetired()
    {
        var callback = Volatile.Read(ref _retirementCallback);
        if (IsRetired && callback is not null && Interlocked.Exchange(ref _retirementNotified, 1) == 0)
            callback(this);
    }

    internal static SharpLinkException Unavailable()
        => new(SharpLinkErrorCode.FailedPrecondition,
            SharpLinkErrorDetails.FailedPrecondition.LocalContractUnavailable,
            "The exact local contract registration is unavailable or retired.");
}
