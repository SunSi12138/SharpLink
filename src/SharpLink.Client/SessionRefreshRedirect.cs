namespace SharpLink.Client;

/// <summary>
/// Stable, depth-bounded indirection from retired session-refresh sources to the newest Ready
/// connection that continues their lineage.
/// </summary>
/// <remarks>
/// Every connection retired along one refresh lineage shares a single instance and only the
/// latest target is retained. The redirect graph therefore stays constant-depth no matter how
/// many rolling refreshes complete while a long-lived caller keeps an older generation pinned:
/// a stale snapshot always reaches the newest Ready connection instead of walking a chain of
/// disposed predecessors.
/// </remarks>
internal sealed class SessionRefreshRedirect
{
    private ClientConnection? _current;

    internal ClientConnection? Current => Volatile.Read(ref _current);

    internal void Publish(ClientConnection connection) => Volatile.Write(ref _current, connection);
}
