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
    void EnsureUserCodeEntry(long requestId)
    {
        // Runtime-only bridge users do not own the Server call-state map. They can still enforce
        // the frozen monotonic deadline carried by the ambient generated invocation context.
        if (SharpLinkCallContext.Current is { } context &&
            context.DeadlineTimeProvider is { } timeProvider &&
            context.LocalRpcDeadline.IsExpired(timeProvider))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded.");
        }
    }

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
