namespace SharpLink.Abstractions;

/// <summary>
/// Provides the Runtime-owned streaming operations required by source-generated server stubs.
/// </summary>
public interface IRpcGeneratedServerBridge
{
    /// <summary>Creates and atomically registers one typed inbound request stream.</summary>
    IAsyncEnumerable<T> CreateInboundStream<T>(
        long requestId,
        ushort streamId,
        IRpcCodec<T> codec,
        bool payloadNullable,
        CancellationToken cancellationToken);

    /// <summary>Pumps one complete outbound response stream, including its terminal state.</summary>
    ValueTask PumpOutboundStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        bool payloadNullable,
        long contractId,
        long methodId,
        CancellationToken cancellationToken);
}
