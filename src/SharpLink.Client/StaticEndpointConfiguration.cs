namespace SharpLink.Client;

internal sealed class StaticEndpointConfiguration(
    SharpLinkEndpoint endpoint,
    IClientTransportFactory transportFactory)
{
    public SharpLinkEndpoint Endpoint { get; private set; } = endpoint;
    public IClientTransportFactory TransportFactory { get; } = transportFactory;

    public void ReplaceEndpoint(SharpLinkEndpoint endpoint)
        => Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
}

/// <summary>Guards the fixed-transport field for resolver-backed clients, which never use it.</summary>
internal sealed class DynamicClusterTransportPlaceholder : IClientTransportFactory
{
    public static readonly DynamicClusterTransportPlaceholder Instance = new();

    private DynamicClusterTransportPlaceholder()
    {
    }

    public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromException<ITransportConnection>(new InvalidOperationException(
            "Resolver-backed clients create endpoint-specific transport factories."));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
