namespace SharpLink.Client;

internal sealed class StaticEndpointConfiguration(
    SharpLinkEndpoint endpoint,
    IClientTransportFactory transportFactory)
{
    public SharpLinkEndpoint Endpoint { get; } = endpoint;
    public IClientTransportFactory TransportFactory { get; } = transportFactory;
}
