namespace SharpLink.Server;

/// <summary>Read-only version inventory operations for a SharpLink server.</summary>
public static class SharpLinkServerVersionInventoryExtensions
{
    /// <summary>Reads the contracts published by this server without making a network request.</summary>
    public static SharpLinkVersionInventory GetLocalVersionInventory(this ISharpLinkServer server)
        => GetBuiltInServer(server).ReadLocalVersionInventory();

    /// <summary>Lists the server's current authenticated, Ready peers.</summary>
    /// <remarks>A peer can disconnect after enumeration. A replacement connection is a different peer.</remarks>
    public static IReadOnlyList<SharpLinkVersionSession> GetVersionPeers(this ISharpLinkServer server)
        => GetBuiltInServer(server).ReadVersionPeers();

    /// <summary>Reads the inventory of the selected physical peer without falling back to another connection.</summary>
    public static ValueTask<SharpLinkVersionInventory> QueryPeerVersionInventoryAsync(
        this ISharpLinkServer server, SharpLinkVersionSession peer, CancellationToken cancellationToken = default)
        => GetBuiltInServer(server).QueryVersionPeerAsync(peer, cancellationToken);

    private static SharpLinkServer GetBuiltInServer(ISharpLinkServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server as SharpLinkServer
            ?? throw new NotSupportedException("This server implementation does not provide version inventory.");
    }
}
