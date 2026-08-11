namespace SharpLink.Server;

/// <summary>
/// Binds generated streaming operations to one Server connection. Runtime owns protocol pumping;
/// this invocation-layer bridge owns business exception mapping.
/// </summary>
internal sealed class ServerGeneratedBridge(
    SharpLinkServer server,
    RpcSession session,
    StripedLongMap<ServerCallCancellationState> callCancellations) : IRpcGeneratedServerBridge
{
    private readonly RpcSessionGeneratedServerBridge _protocolBridge = new(session);

    public IAsyncEnumerable<T> CreateInboundStream<T>(
        long requestId,
        ushort streamId,
        IRpcCodec<T> codec,
        bool payloadNullable,
        CancellationToken cancellationToken)
        => _protocolBridge.CreateInboundStream(
            requestId,
            streamId,
            codec,
            payloadNullable,
            cancellationToken);

    public async ValueTask PumpOutboundStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        bool payloadNullable,
        long contractId,
        long methodId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _protocolBridge.PumpOutboundStreamAsync(
                requestId,
                streamId,
                stream,
                codec,
                payloadNullable,
                contractId,
                methodId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            var protocolError = server.MapStreamServiceException(
                callCancellations,
                session,
                requestId,
                contractId,
                methodId,
                exception);
            session.SendStreamErrorAsync(requestId, streamId, protocolError);
        }
    }
}
