namespace SharpLink.Client;

/// <summary>Selects one physical Ready connection on the client that returned it.</summary>
/// <remarks>Holding this value does not keep a connection alive. A reconnect requires a new selector.</remarks>
public sealed class SharpLinkVersionConnection
{
    private readonly WeakReference<ClientConnection> _connection;

    internal SharpLinkVersionConnection(ClientConnection connection, string? endpointId, SharpLinkVersionSession? observedSession)
        => (_connection, EndpointId, ObservedSession) = (new(connection), endpointId, observedSession);

    /// <summary>Gets the configured endpoint ID, when present. Several connections may share this value.</summary>
    public string? EndpointId { get; }

    /// <summary>
    /// Gets the remote physical identity if previously observed when the selector was listed.
    /// Query inventory on this selector to learn it without preparing an RPC.
    /// </summary>
    public SharpLinkVersionSession? ObservedSession { get; }

    internal bool TryGetConnection(out ClientConnection connection) => _connection.TryGetTarget(out connection!);
}
