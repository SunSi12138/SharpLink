namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal CancellationTokenRegistration RegisterWriterOwnedStreamCancellation(
        StreamFlowController.ResolvedSendCreditLease lease, CancellationToken cancellationToken)
    {
        if (!UsesWriterOwnedStreams || !cancellationToken.CanBeCanceled ||
            lease.State is not WriterOwnedStreamScheduler.StreamHandle stream)
            return default;
        return cancellationToken.UnsafeRegister(static (state, token) =>
        {
            var handle = (WriterOwnedStreamScheduler.StreamHandle)state!;
            // The immutable handle, not its reusable wire key, owns this cancellation.
            // Abort discards only preparation; admitted frames remain pinned by the writer.
            handle.Owner.Abort(handle, new OperationCanceledException(token));
        }, stream);
    }

    private static void AbortWriterOwnedLease(
        StreamFlowController.ResolvedSendCreditLease lease, Exception error)
    {
        if (lease.State is WriterOwnedStreamScheduler.StreamHandle stream)
            stream.Owner.Abort(stream, error);
    }

    internal ValueTask SendClientStreamCompleteWithWriterAsync(
        long requestId, ushort streamId, RpcDeadline deadline, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (UsesWriterOwnedStreams)
            return SendWriterOwnedTerminalAsync(requestId, streamId, null,
                deadline, timeProvider, cancellationToken);
        SendClientStreamComplete(requestId, streamId, deadline, timeProvider, cancellationToken);
        return ValueTask.CompletedTask;
    }

    internal ValueTask SendClientStreamErrorWithWriterAsync(
        long requestId, ushort streamId, SharpLinkException error, RpcDeadline deadline,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (UsesWriterOwnedStreams)
            return SendWriterOwnedTerminalAsync(requestId, streamId, error,
                deadline, timeProvider, cancellationToken);
        SendClientStreamError(requestId, streamId, error, deadline, timeProvider, cancellationToken);
        return ValueTask.CompletedTask;
    }
}
