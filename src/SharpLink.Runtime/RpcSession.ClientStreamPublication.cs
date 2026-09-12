namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal ValueTask SendClientStreamChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken terminalToken)
        => SendClientStreamChunkAsync(
            requestId,
            streamId,
            item,
            RuntimeContext.Codecs.GetCodec<T>(),
            deadline,
            timeProvider,
            terminalToken);

    internal ValueTask SendClientStreamChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken terminalToken)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ThrowIfClientStreamPublicationRejected(deadline, timeProvider, terminalToken);

        if (codec is IRpcSizedCodec<T> sizedCodec &&
            sizedCodec.CanExactSize &&
            sizedCodec.TryGetEncodedSize(item, out var knownEncodedBytes, out var sizedSnapshot))
        {
            return SendClientStreamChunkKnownSizeAsync(
                requestId,
                streamId,
                item,
                sizedCodec,
                knownEncodedBytes,
                sizedSnapshot,
                deadline,
                timeProvider,
                terminalToken);
        }

        return SendClientUnsizedStreamChunkAsync(
            requestId,
            streamId,
            item,
            codec,
            deadline,
            timeProvider,
            terminalToken);
    }

    internal void SendClientStreamComplete(
        long requestId,
        ushort streamId,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken terminalToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var writer = RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.StreamComplete,
                       ProtocolV2FrameFlags.None,
                       unchecked((ulong)requestId)))
            {
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                writer.Advance(sizeof(ushort));
            }

            try
            {
                // This is the clean-EOF publication commit. User production may have finished
                // before the call became terminal, so re-arbitrate immediately before enqueue.
                ThrowIfClientStreamPublicationRejected(deadline, timeProvider, terminalToken);
                ownsWriter = false;
                SendPacket(writer);
            }
            finally
            {
                CompleteSendStream(requestId, streamId);
            }
        }
        finally
        {
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    internal void SendClientStreamError(
        long requestId,
        ushort streamId,
        SharpLinkException exception,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken terminalToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var writer = RentFrameWriter();
        var ownsWriter = true;
        try
        {
            var packet = writer.BeginPacket(
                ProtocolV2FrameType.StreamComplete,
                ProtocolV2FrameFlags.Error,
                unchecked((ulong)requestId));
            var idSpan = writer.GetSpan(sizeof(ushort));
            BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
            writer.Advance(sizeof(ushort));
            ProtocolV2PayloadCodec.WriteError(
                writer,
                exception.Code,
                exception.Message,
                RuntimeContext.Protocol.MaxErrorMessageBytes,
                out var truncated);
            writer.EndPacket(packet);
            if (truncated)
                writer.WrittenSpan[packet.StartOffset + 6] |= (byte)ProtocolV2FrameFlags.Truncated;

            try
            {
                // Error-form StreamComplete is still stream progress. A logical terminal that
                // already owns the call must suppress this late wire terminal as well.
                ThrowIfClientStreamPublicationRejected(deadline, timeProvider, terminalToken);
                ownsWriter = false;
                SendPacket(writer);
            }
            finally
            {
                CompleteSendStream(requestId, streamId, exception);
            }
        }
        finally
        {
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private async ValueTask SendClientStreamChunkKnownSizeAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcSizedCodec<T> sizedCodec,
        int encodedBytes,
        IRpcSizedCodecSnapshot? sizedSnapshot,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken terminalToken)
    {
        var creditBytes = Math.Max(1, encodedBytes);
        var creditAcquired = false;
        IRpcByteBufferWriter? writer = null;
        var ownsWriter = true;
        try
        {
            await AcquireStreamSendCreditAsync(
                requestId,
                streamId,
                creditBytes,
                terminalToken).ConfigureAwait(false);
            creditAcquired = true;

            writer = RuntimeContext.Buffers.Rent(
                checked(ProtocolV2Constants.HeaderBytes + NegotiatedMaxFramePayloadBytes + 4));
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.StreamData,
                       ProtocolV2FrameFlags.None,
                       unchecked((ulong)requestId)))
            {
                writer.GetSpan(sizeof(ushort) + encodedBytes + 4);
                writer.Advance(0);
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                writer.Advance(sizeof(ushort));
                sizedCodec.SerializeSized(item, writer, encodedBytes, sizedSnapshot);
                if (sizedSnapshot is not null)
                {
                    sizedCodec.ReleaseSnapshot(sizedSnapshot);
                    sizedSnapshot = null;
                }
            }

            var actualEncodedBytes = writer.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort);
            if (actualEncodedBytes != encodedBytes)
            {
                throw new InvalidOperationException(
                    "Client stream item size differed after credit was acquired.");
            }

            ThrowIfClientStreamPublicationRejected(deadline, timeProvider, terminalToken);
            ownsWriter = false;
            SendPacket(writer);
        }
        catch
        {
            if (creditAcquired)
                ReturnUnsentStreamCredit(requestId, streamId, creditBytes);
            throw;
        }
        finally
        {
            if (sizedSnapshot is not null)
                sizedCodec.ReleaseSnapshot(sizedSnapshot);
            if (ownsWriter && writer is not null)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private ValueTask SendClientUnsizedStreamChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken terminalToken)
    {
        IRpcByteBufferWriter? writer = null;
        var ownsWriter = true;
        var creditAcquired = false;
        try
        {
            if (Volatile.Read(ref _terminal) is { } terminal)
                throw terminal.Exception;
            ThrowIfClientStreamPublicationRejected(deadline, timeProvider, terminalToken);

            writer = RentFrameWriter();
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.StreamData,
                       ProtocolV2FrameFlags.None,
                       unchecked((ulong)requestId)))
            {
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                writer.Advance(sizeof(ushort));
                codec.Serialize(item, writer);
            }

            var encodedBytes = Math.Max(
                1,
                writer.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort));
            ThrowIfClientStreamPublicationRejected(deadline, timeProvider, terminalToken);

            if (!HasStreamFlowControl ||
                TryAcquireStreamSendCredit(requestId, streamId, encodedBytes))
            {
                creditAcquired = HasStreamFlowControl;
                try
                {
                    ThrowIfClientStreamPublicationRejected(deadline, timeProvider, terminalToken);
                    ownsWriter = false;
                    SendPacket(writer);
                }
                catch
                {
                    if (creditAcquired)
                        ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
                    throw;
                }
                return ValueTask.CompletedTask;
            }

            var budget = GetOrCreatePreCreditSerializedBudget();
            var pendingBudget = budget.AcquireAsync(
                requestId,
                streamId,
                encodedBytes,
                terminalToken);
            ownsWriter = false;
            return AwaitClientPreCreditBudgetAndFlowCreditAsync(
                pendingBudget,
                writer,
                requestId,
                streamId,
                encodedBytes,
                budget,
                deadline,
                timeProvider,
                terminalToken);
        }
        finally
        {
            if (ownsWriter && writer is not null)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private async ValueTask AwaitClientPreCreditBudgetAndFlowCreditAsync(
        ValueTask pendingBudget,
        IRpcByteBufferWriter writer,
        long requestId,
        ushort streamId,
        int encodedBytes,
        PreCreditSerializedBudget budget,
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken terminalToken)
    {
        var ownsWriter = true;
        var ownsBudget = false;
        var creditAcquired = false;
        try
        {
            await pendingBudget.ConfigureAwait(false);
            ownsBudget = true;

            await AcquireStreamSendCreditAsync(
                requestId,
                streamId,
                encodedBytes,
                terminalToken).ConfigureAwait(false);
            creditAcquired = true;

            budget.Release(encodedBytes);
            ownsBudget = false;

            ThrowIfClientStreamPublicationRejected(deadline, timeProvider, terminalToken);
            ownsWriter = false;
            SendPacket(writer);
        }
        catch
        {
            if (creditAcquired)
                ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
            throw;
        }
        finally
        {
            if (ownsBudget)
                budget.Release(encodedBytes);
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private static void ThrowIfClientStreamPublicationRejected(
        RpcDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken terminalToken)
    {
        terminalToken.ThrowIfCancellationRequested();
        if (deadline.IsExpired(timeProvider))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "RPC deadline exceeded during client stream publication.");
        }
    }
}
