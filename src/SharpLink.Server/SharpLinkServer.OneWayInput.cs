namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private static void ConfigureBoundOneWayInputProgress(RpcSession session, long requestId, int count,
        StripedLongMap<ServerCallCancellationState> cancellations)
    {
        if (count != 0) ConfigureBoundOneWayInputProgressCore(session, requestId, count, cancellations);
    }
    // Keep captured state and its allocation off plain OneWay. This callback retains only the
    // stable scalar request/map, never a pooled state generation or collectible registration.
    private static void ConfigureBoundOneWayInputProgressCore(RpcSession session, long requestId, int count,
        StripedLongMap<ServerCallCancellationState> cancellations)
        => session.StreamManager.ConfigureOneWayInputProgress(requestId, count,
            () => TryAcceptInboundStreamProgress(cancellations, requestId, out _));
}
