namespace SharpLink.Runtime;

internal sealed partial class WriterOwnedStreamScheduler
{
    // The predecessor is the durable emission result of the real Request frame, not merely
    // its producer-side enqueue result. A ready lane may otherwise overtake a queued Request.
    // Do not consume stream capacity, construct a handle or queue DATA before it succeeds.
    internal async ValueTask<StreamHandle> OpenAfterRequestEmissionAsync(
        long requestId,
        ushort streamId,
        Task requestEmission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestEmission);
        try
        {
            await requestEmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Canceling this observer does not cancel the pump's ownership of the Request.
            // Observe a later output failure even after the caller stopped waiting.
            _ = ObserveAbandonedDrainAsync(requestEmission);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await OpenAsync(requestId, streamId, cancellationToken).ConfigureAwait(false);
    }
}
