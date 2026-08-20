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
        var (interfaceHash, methodHash, deadline) = ReadRoutingPrefix(ref reader, flags, timeProvider);

        SharpLinkMetadata? metadata = null;
        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0)
        {
            var metadataLength = ReadMetadataLength(session, ref reader, maxMetadataBytes);
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

    internal static ServerRequestEnvelope ReadRouting(
        RpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        int maxMetadataBytes,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var reader = new SequenceReader<byte>(payload);
        var (interfaceHash, methodHash, deadline) = ReadRoutingPrefix(ref reader, flags, timeProvider);

        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0)
        {
            var metadataLength = ReadMetadataLength(session, ref reader, maxMetadataBytes);
            reader.Advance(metadataLength);
        }

        return new ServerRequestEnvelope(
            interfaceHash,
            methodHash,
            reader.UnreadSequence,
            deadline,
            Metadata: null);
    }

    private static (long InterfaceHash, long MethodHash, RpcDeadline Deadline) ReadRoutingPrefix(
        ref SequenceReader<byte> reader,
        ProtocolV2FrameFlags flags,
        TimeProvider timeProvider)
    {
        if (!reader.TryReadLittleEndian(out long interfaceHash) ||
            !reader.TryReadLittleEndian(out long methodHash))
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.MalformedFrame,
                "Request routing prefix is truncated.");
        }

        var deadline = default(RpcDeadline);
        if ((flags & ProtocolV2FrameFlags.HasDeadline) != 0)
        {
            if (!reader.TryReadLittleEndian(out long unixMilliseconds))
            {
                throw new SharpLinkProtocolViolationException(
                    ProtocolViolationReason.MalformedFrame,
                    "Request deadline is truncated.");
            }
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

        return (interfaceHash, methodHash, deadline);
    }

    private static uint ReadMetadataLength(
        RpcSession session,
        ref SequenceReader<byte> reader,
        int maxMetadataBytes)
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
        return metadataLength;
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
