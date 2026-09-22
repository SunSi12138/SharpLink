namespace SharpLink.Abstractions;

/// <summary>
/// Provides the Runtime-owned streaming operations required by source-generated server stubs.
/// </summary>
public interface IRpcGeneratedServerBridge
{
    /// <summary>
    /// Claims one boundary at which framework code is about to re-enter user code for a request.
    /// Throws the already-selected call terminal when user code may no longer run.
    /// </summary>
    void EnsureUserCodeEntry(long requestId);

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

    /// <summary>Creates a generated inbound stream while preserving a statically known Codec type.</summary>
    IAsyncEnumerable<T> CreateGeneratedInboundStream<T, TCodec>(
        long requestId,
        ushort streamId,
        in TCodec codec,
        bool payloadNullable,
        CancellationToken cancellationToken)
        where TCodec : IRpcCodec<T>
        => CreateInboundStream(requestId, streamId, codec, payloadNullable, cancellationToken);

    /// <summary>Pumps a generated outbound stream while preserving a statically known Codec type.</summary>
    ValueTask PumpGeneratedOutboundStreamAsync<T, TCodec>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        in TCodec codec,
        bool payloadNullable,
        long contractId,
        long methodId,
        CancellationToken cancellationToken)
        where TCodec : IRpcCodec<T>
        => PumpOutboundStreamAsync(
            requestId,
            streamId,
            stream,
            codec,
            payloadNullable,
            contractId,
            methodId,
            cancellationToken);
}
