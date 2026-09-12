using System.Collections.Concurrent;

namespace SharpLink.Server;

/// <summary>
/// Owns active and retired connection membership for one <see cref="SharpLinkServer"/>.
/// Active entries are keyed by session id; retired entries retain framework ownership until
/// connection-scoped service cleanup has completed.
/// </summary>
internal sealed class ServerConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ServerConnectionState> _active = [];
    private readonly ConcurrentDictionary<ServerConnectionState, byte> _retired = [];

    internal int Count => _active.Count;

    internal ICollection<ServerConnectionState> Values => _active.Values;

    internal bool TryAdd(string id, ServerConnectionState connection)
        => _active.TryAdd(id, connection);

    internal bool TryGetValue(string id, out ServerConnectionState connection)
    {
        if (_active.TryGetValue(id, out var current))
        {
            connection = current;
            return true;
        }

        connection = null!;
        return false;
    }

    /// <summary>
    /// Replaces only the expected current connection so a stale retirement cannot overwrite a
    /// newer connection that reused the same session id.
    /// </summary>
    internal bool TryUpdate(
        string id,
        ServerConnectionState connection,
        ServerConnectionState expected)
        => _active.TryUpdate(id, connection, expected);

    internal bool TryRemove(string id, out ServerConnectionState connection)
    {
        if (_active.TryRemove(id, out var removed))
        {
            connection = removed;
            return true;
        }

        connection = null!;
        return false;
    }

    /// <summary>
    /// Removes an active entry only when both the session id and connection instance still match.
    /// </summary>
    internal bool TryRemove(KeyValuePair<string, ServerConnectionState> connection)
        => ((ICollection<KeyValuePair<string, ServerConnectionState>>)_active).Remove(connection);

    /// <summary>
    /// Retains a retiring connection exactly once until its service-cleanup owner releases it.
    /// </summary>
    internal bool TryRetire(ServerConnectionState connection)
        => _retired.TryAdd(connection, 0);

    internal bool CompleteRetired(ServerConnectionState connection)
        => _retired.TryRemove(connection, out _);

    internal bool IsRetired(ServerConnectionState connection)
        => _retired.ContainsKey(connection);

    /// <summary>
    /// Enumerates active entries with the weakly-consistent semantics of
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> without materializing a snapshot.
    /// </summary>
    internal IEnumerable<KeyValuePair<string, ServerConnectionState>> EnumerateActiveEntries()
        => _active;

    /// <summary>Returns a point-in-time snapshot of active connection ownership.</summary>
    internal ServerConnectionState[] SnapshotActive()
        => _active.Values.ToArray();

    /// <summary>
    /// Returns every connection still owned by the server, de-duplicating a connection that is
    /// momentarily visible in both active and retired membership during retirement.
    /// </summary>
    internal ServerConnectionState[] SnapshotOwned()
        => _active.Values.Concat(_retired.Keys).Distinct().ToArray();
}
