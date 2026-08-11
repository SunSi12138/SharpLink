namespace SharpLink.Server;

internal static class ServerRequestEnvelopeReader
{
    internal static ServerRequestEnvelope Read(
        RpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        int maxMetadataBytes,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long interfaceHash) ||
            !reader.TryReadLittleEndian(out long methodHash))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ProtocolViolation,
                "Request routing prefix is truncated.");
        }

        var deadline = default(RpcDeadline);
        if ((flags & ProtocolV2FrameFlags.HasDeadline) != 0)
        {
            if (!reader.TryReadLittleEndian(out long unixMilliseconds))
                throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "Request deadline is truncated.");
            try
            {
                var utcDeadline = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
                deadline = RpcDeadline.Create(
                    utcDeadline,
                    timeProvider.GetUtcNow(),
                    timeProvider.GetTimestamp(),
                    timeProvider.TimestampFrequency);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Request deadline is outside the supported UTC range.",
                    exception);
            }
        }

        SharpLinkMetadata? metadata = null;
        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0)
        {
            if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.Metadata) == 0)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Request metadata was not negotiated during handshake.");
            }
            if (!ProtocolV2PayloadCodec.TryReadVarUInt32(ref reader, out var metadataLength) ||
                metadataLength > maxMetadataBytes ||
                reader.Remaining < metadataLength)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
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
    SharpLinkMetadata? Metadata)
{
    internal DateTimeOffset? Deadline => RpcDeadline.UtcDeadline;
}
