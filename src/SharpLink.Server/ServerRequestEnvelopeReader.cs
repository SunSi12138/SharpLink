namespace SharpLink.Server;

internal static class ServerRequestEnvelopeReader
{
    internal static ServerRequestEnvelope Read(
        RpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        int maxMetadataBytes,
        TimeProvider timeProvider,
        RpcDeadline resolvedDeadline = default)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long interfaceHash) ||
            !reader.TryReadLittleEndian(out long methodHash))
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.MalformedFrame,
                "Request routing prefix is truncated.");
        }

        var deadline = resolvedDeadline;
        if ((flags & ProtocolV2FrameFlags.HasTimeBudget) != 0)
        {
            if (!reader.TryReadLittleEndian(out long timeBudgetTicks))
            {
                throw new SharpLinkProtocolViolationException(
                    ProtocolViolationReason.MalformedFrame,
                    "Request time budget is truncated.");
            }
            if (timeBudgetTicks < 0)
            {
                throw new SharpLinkProtocolViolationException(
                    ProtocolViolationReason.MalformedFrame,
                    "Request time budget cannot be negative.");
            }
            if (!deadline.HasValue)
                deadline = RpcDeadline.Create(TimeSpan.FromTicks(timeBudgetTicks), timeProvider);
        }

        SharpLinkMetadata? metadata = null;
        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0)
        {
            if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.Metadata) == 0)
            {
                throw new SharpLinkProtocolViolationException(
                    ProtocolViolationReason.ProtocolState,
                    "Request metadata was not negotiated during handshake.");
            }
            if (!ProtocolV2PayloadCodec.TryReadVarUInt32(ref reader, out var metadataLength) ||
                metadataLength > maxMetadataBytes ||
                reader.Remaining < metadataLength)
            {
                throw new SharpLinkProtocolViolationException(
                    ProtocolViolationReason.MalformedFrame,
                    "Request metadata length is invalid.");
            }
            metadata = ProtocolV2PayloadCodec.ReadMetadata(
                reader.Sequence.Slice(reader.Position, metadataLength));
            reader.Advance(metadataLength);
        }

        return new ServerRequestEnvelope(
            interfaceHash,
            methodHash,
            reader.UnreadSequence,
            deadline,
            metadata);
    }
}

internal readonly record struct ServerRequestEnvelope(
    long InterfaceHash,
    long MethodHash,
    ReadOnlySequence<byte> Arguments,
    RpcDeadline RpcDeadline,
    SharpLinkMetadata? Metadata);
