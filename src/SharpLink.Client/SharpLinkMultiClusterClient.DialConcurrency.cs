namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    private ClusterDialConcurrencyLimiter? _clusterDialLimiter;

    private void InitializeClusterDialLimiter(
        SharpLinkMultiClusterOptions options,
        IEnumerable<SharpLinkClusterSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(slots);
        _clusterDialLimiter = new ClusterDialConcurrencyLimiter(options.MaxConcurrentClusterConnects);
        foreach (var slot in slots)
            BindClusterDialLimiter(slot);
    }

    private void BindClusterDialLimiter(SharpLinkClusterSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Client is not SharpLinkClient child)
            return;

        child.BindMultiClusterDialLimiter(
            _clusterDialLimiter ?? throw new InvalidOperationException(
                "The multi-cluster dial limiter has not been initialized."));
    }
}
