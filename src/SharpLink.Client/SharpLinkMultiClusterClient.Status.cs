namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    public bool TryGetClusterStatus(
        SharpLinkClusterKey cluster,
        out SharpLinkClusterStatusSnapshot status)
    {
        if (!SharpLinkClusterKey.IsValid(cluster.Value))
        {
            throw new ArgumentException(
                "A valid non-default SharpLinkClusterKey is required.",
                nameof(cluster));
        }

        var snapshot = Volatile.Read(ref _snapshot);
        if (!snapshot.Clusters.TryGetValue(cluster, out var slot))
        {
            status = default;
            return false;
        }

        status = new SharpLinkClusterStatusSnapshot(cluster, slot.Client.State);
        return true;
    }
}
