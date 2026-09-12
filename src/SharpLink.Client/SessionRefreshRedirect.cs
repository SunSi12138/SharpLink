namespace SharpLink.Client;

/// <summary>
/// Stable, depth-bounded indirection from retired session-refresh sources to the newest Ready
/// connection that continues their lineage.
/// </summary>
/// <remarks>
/// Every connection retired along one refresh lineage shares a single instance and only the
/// latest target is retained. Its initial target is the source; publishing a replacement both
/// closes source admission and opens replacement admission at the same linearization point.
/// The redirect graph therefore stays constant-depth no matter how
/// many rolling refreshes complete while a long-lived caller keeps an older generation pinned:
/// a stale snapshot always reaches the newest Ready connection instead of walking a chain of
/// disposed predecessors.
/// </remarks>
internal sealed class SessionRefreshRedirect(ClientConnection current)
{
    private ClientConnection _current = current;

    internal ClientConnection Current => Volatile.Read(ref _current);

    internal void Publish(ClientConnection connection) => Volatile.Write(ref _current, connection);
}
