namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>Provides the cluster-specific portion of client connection routing.</summary>
    private interface IEndpointClusterRuntime
    {
        int ReadyConnectionCount { get; }
        int PendingCallCount { get; }
        int ActiveCallCount { get; }
        int ActiveStreamCount { get; }
        ValueTask ConnectAsync(CancellationToken cancellationToken);
        ClientConnection GetReadyConnection();
        void MarkConnectionDraining(ClientConnection connection);
        void RetireDrainingConnectionIfIdle(ClientConnection connection);
        ValueTask StopAsync();
    }
}
