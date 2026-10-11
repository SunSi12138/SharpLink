namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private async ValueTask<StreamFlowController.ResolvedSendCreditLease> SendWriterOwnedChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        IRpcSizedCodec<T>? exactSizeCodec,
        CancellationToken cancellationToken,
        StreamFlowController.ResolvedSendCreditLease lease,
        RpcDeadline deadline,
        TimeProvider? deadlineTimeProvider)
    {
        var stream = await ResolveWriterOwnedStreamAsync(requestId, streamId, lease, cancellationToken).ConfigureAwait(false);
        await EnqueueWriterOwnedItem(stream, item, codec, exactSizeCodec,
            cancellationToken, deadline, deadlineTimeProvider).ConfigureAwait(false);
        // Reuse the existing immutable lease's object slot; no extra per-item wrapper allocation.
        // The connection identity remains the negotiated controller, but the state is explicitly
        // tagged by its runtime type. Producer-side controller APIs reject this mode entirely.
        return new StreamFlowController.ResolvedSendCreditLease(
            Volatile.Read(ref _protocolState).FlowController!, requestId, streamId, stream, stream.Generation);
    }

    private async ValueTask<WriterOwnedStreamScheduler.StreamHandle> ResolveWriterOwnedStreamAsync(
        long requestId,
        ushort streamId,
        StreamFlowController.ResolvedSendCreditLease lease,
        CancellationToken cancellationToken)
    {
        var owner = GetOrCreateWriterOwnedStreams();
        cancellationToken.ThrowIfCancellationRequested();
        if (lease.IsResolved)
        {
            if (!ReferenceEquals(lease.Owner, Volatile.Read(ref _protocolState).FlowController) ||
                lease.State is not WriterOwnedStreamScheduler.StreamHandle stream ||
                !ReferenceEquals(stream.Owner, owner) || stream.RequestId != requestId ||
                stream.StreamId != streamId || stream.Generation != lease.Generation)
            {
                throw new InvalidOperationException("A stream sender lease belongs to another connection or lifecycle.");
            }
            return stream;
        }
        if (Role == RpcSessionRole.Client)
        {
            // Existing client production starts after Request enqueue. This ordinary-lane
            // marker is the durable predecessor; ready DATA cannot overtake that Request.
            var emission = FlushSendQueueAsync(cancellationToken).AsTask();
            return await owner.OpenAfterRequestEmissionAsync(
                requestId, streamId, emission, cancellationToken).ConfigureAwait(false);
        }
        return await owner.OpenAsync(requestId, streamId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendWriterOwnedTerminalAsync(
        long requestId,
        ushort streamId,
        SharpLinkException? error,
        RpcDeadline deadline,
        TimeProvider? deadlineTimeProvider,
        CancellationToken cancellationToken)
    {
        var stream = await ResolveWriterOwnedStreamAsync(requestId, streamId, default, cancellationToken).ConfigureAwait(false);
        IRpcByteBufferWriter? packet = null;
        var ownsPacket = true;
        try
        {
            packet = RentFrameWriter();
            var scope = packet.BeginPacket(ProtocolV2FrameType.StreamComplete,
                error is null ? ProtocolV2FrameFlags.None : ProtocolV2FrameFlags.Error,
                unchecked((ulong)requestId));
            BinaryPrimitives.WriteUInt16LittleEndian(packet.GetSpan(sizeof(ushort)), streamId);
            packet.Advance(sizeof(ushort));
            var truncated = false;
            if (error is not null)
                ProtocolV2PayloadCodec.WriteError(packet, error.Code, error.Message,
                    RuntimeContext.Protocol.MaxErrorMessageBytes, out truncated);
            packet.EndPacket(scope);
            if (truncated) packet.WrittenSpan[scope.StartOffset + 6] |= (byte)ProtocolV2FrameFlags.Truncated;
            packet = PrepareOutboundPacket(packet, cancellationToken);
            ownsPacket = false;
            ValidateOutboundPacketOrReturn(packet, allowEmpty: false);
            ownsPacket = true;
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            ownsPacket = false;
            var queued = stream.Owner.EnqueueAsync(stream, packet, 0, terminal: true,
                cancellationToken: cancellationToken, deadline: deadline, deadlineTimeProvider: deadlineTimeProvider);
            await AwaitWriterOwnedTerminalStepAsync(queued, stream, deadline, deadlineTimeProvider, cancellationToken)
                .ConfigureAwait(false);
            // This is a per-stream join, never a per-item flush. It prevents successful
            // completion/disposal from canceling preparation while the EOF still waits for DATA.
            var drained = new ValueTask(stream.Owner.DrainAcceptedFramesAsync(stream, cancellationToken));
            await AwaitWriterOwnedTerminalStepAsync(drained, stream, deadline, deadlineTimeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            stream.Owner.Abort(stream, failure);
            throw;
        }
        finally
        {
            if (ownsPacket && packet is not null) RuntimeContext.Buffers.Return(packet);
        }
    }

    private static async ValueTask AwaitWriterOwnedTerminalStepAsync(
        ValueTask operation,
        WriterOwnedStreamScheduler.StreamHandle stream,
        RpcDeadline deadline,
        TimeProvider? timeProvider,
        CancellationToken cancellationToken)
    {
        if (operation.IsCompletedSuccessfully || !deadline.HasValue || timeProvider is null)
        {
            await operation.ConfigureAwait(false);
            return;
        }
        var task = operation.AsTask();
        try
        {
            if (!await SharpLinkTimer.WaitAsync(task, deadline, timeProvider, cancellationToken).ConfigureAwait(false))
            {
                var error = CreateGeneratedStreamDeadlineExceededException();
                stream.Owner.Abort(stream, error);
                throw error;
            }
            await task.ConfigureAwait(false);
        }
        catch
        {
            _ = ObserveAbandonedGeneratedSendAsync(task);
            throw;
        }
    }

    internal ValueTask SendStreamErrorWithWriterAsync(
        long requestId, ushort streamId, SharpLinkException error, CancellationToken cancellationToken)
    {
        if (!UsesWriterOwnedStreams)
        {
            SendStreamErrorAsync(requestId, streamId, error);
            return ValueTask.CompletedTask;
        }
        var context = SharpLinkCallContext.Current;
        return SendWriterOwnedTerminalAsync(requestId, streamId, error,
            context?.LocalRpcDeadline ?? default, context?.DeadlineTimeProvider, cancellationToken);
    }
}
