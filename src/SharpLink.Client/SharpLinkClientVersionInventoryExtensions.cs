namespace SharpLink.Client;

/// <summary>Read-only version inventory operations for a SharpLink client.</summary>
public static class SharpLinkClientVersionInventoryExtensions
{
    /// <summary>Reads the contracts published by this client without making a network request.</summary>
    public static SharpLinkVersionInventory GetLocalVersionInventory(this ISharpLinkClient client)
        => GetBuiltInClient(client).ReadLocalVersionInventory();

    /// <summary>Lists current physical selectors without querying peers or preparing RPCs.</summary>
    public static IReadOnlyList<SharpLinkVersionConnection> GetVersionConnections(this ISharpLinkClient client)
        => GetBuiltInClient(client).ReadVersionConnections();

    /// <summary>Reads peer inventory on this exact current connection, without falling back to another connection.</summary>
    public static ValueTask<SharpLinkVersionInventory> QueryPeerVersionInventoryAsync(
        this ISharpLinkClient client, SharpLinkVersionConnection connection, CancellationToken cancellationToken = default)
        => GetBuiltInClient(client).QueryVersionConnectionAsync(connection, cancellationToken);

    /// <summary>Reads the server's inventory on this client's single Ready connection.</summary>
    /// <remarks>
    /// Requires exactly one Ready connection for this operation. Reading inventory does not prepare a proxy
    /// or authorize a call.
    /// </remarks>
    public static async ValueTask<SharpLinkVersionInventory> QueryPeerVersionInventoryAsync(
        this ISharpLinkClient client, CancellationToken cancellationToken = default)
        => (await GetBuiltInClient(client).QueryNStudyRemoteInventoryAsync(cancellationToken).ConfigureAwait(false))
            .ToVersionInventory(remote: true);

    private static SharpLinkClient GetBuiltInClient(ISharpLinkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client as SharpLinkClient
            ?? throw new NotSupportedException("This client implementation does not provide version inventory.");
    }
}
