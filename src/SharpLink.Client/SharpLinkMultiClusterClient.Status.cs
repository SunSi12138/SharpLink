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

        var child = slot.Client;
        status = new SharpLinkClusterStatusSnapshot(
            cluster,
            child.State,
            child.ClusterState,
            child.Readiness);
        return true;
    }
}
