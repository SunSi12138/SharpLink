namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    // Only the returned, validated packet crosses into the writer. All codec, snapshot,
    // compression, TimeProvider and cancellation checks execute on the producer side.
    private ValueTask EnqueueWriterOwnedItem<T>(
        WriterOwnedStreamScheduler.StreamHandle stream,
        T item,
        IRpcCodec<T> codec,
        IRpcSizedCodec<T>? exactSizeCodec,
        CancellationToken cancellationToken,
        RpcDeadline deadline,
        TimeProvider? deadlineTimeProvider)
    {
        IRpcByteBufferWriter? packet = null;
        IRpcSizedCodecSnapshot? snapshot = null;
        var ownsPacket = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            var encodedBytes = 0;
            var sized = exactSizeCodec is not null &&
                exactSizeCodec.TryGetEncodedSize(item, out encodedBytes, out snapshot);
            if (sized && (encodedBytes < 0 || encodedBytes > NegotiatedMaxFramePayloadBytes - sizeof(ushort)))
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "Encoded stream item exceeds its negotiated frame limit.");

            // Sized codecs may use their documented four-byte overstore padding; the wire
            // length and flow debit below still exclude that padding and the stream id.
            packet = RuntimeContext.Buffers.Rent(
                checked(ProtocolV2Constants.HeaderBytes + NegotiatedMaxFramePayloadBytes + (sized ? 4 : 0)));
            using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData,
                ProtocolV2FrameFlags.None, unchecked((ulong)stream.RequestId)))
            {
                if (sized) packet.GetSpan(checked(sizeof(ushort) + encodedBytes + 4));
                BinaryPrimitives.WriteUInt16LittleEndian(packet.GetSpan(sizeof(ushort)), stream.StreamId);
                packet.Advance(sizeof(ushort));
                if (sized) exactSizeCodec!.SerializeSized(item, packet, encodedBytes, snapshot);
                else codec.Serialize(item, packet);
            }
            var actual = packet.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort);
            if (sized && actual != encodedBytes)
                throw new InvalidOperationException("Stream item size differed from its exact-size snapshot.");
            if (actual < 0 || actual > NegotiatedMaxFramePayloadBytes - sizeof(ushort))
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "Encoded stream item exceeds its negotiated frame limit.");
            var creditBytes = Math.Max(1, actual);

            // Clear ownership before invoking an extension: a throwing ReleaseSnapshot must
            // not cause the same snapshot to be released a second time from the finally.
            if (snapshot is not null)
            {
                var released = snapshot;
                snapshot = null;
                exactSizeCodec!.ReleaseSnapshot(released);
            }
            packet = PrepareOutboundPacket(packet, cancellationToken);
            ownsPacket = false; // Validation itself returns the packet on failure.
            ValidateOutboundPacketOrReturn(packet, allowEmpty: false);
            ownsPacket = true;
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            ownsPacket = false;
            return stream.Owner.EnqueueAsync(stream, packet, creditBytes,
                cancellationToken: cancellationToken, deadline: deadline, deadlineTimeProvider: deadlineTimeProvider);
        }
        finally
        {
            try
            {
                if (snapshot is not null) exactSizeCodec!.ReleaseSnapshot(snapshot);
            }
            finally
            {
                if (ownsPacket && packet is not null) RuntimeContext.Buffers.Return(packet);
            }
        }
    }

    private void RejectWriterOwnedStreamBypass(IRpcByteBufferWriter packet)
    {
        if (UsesWriterOwnedStreams && packet.WrittenCount >= ProtocolV2Constants.HeaderBytes &&
            (ProtocolV2FrameType)packet.WrittenSpan[5] is
                ProtocolV2FrameType.StreamData or ProtocolV2FrameType.StreamComplete)
        {
            throw new InvalidOperationException("Stream frames on a writer-owned connection must use its ordered stream sender.");
        }
    }
}
