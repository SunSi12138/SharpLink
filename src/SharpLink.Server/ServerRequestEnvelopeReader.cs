namespace SharpLink.Server;

internal static class ServerRequestEnvelopeReader
{
    /// <summary>Rebinds arguments only while both the encoded and decoded owners are alive.</summary>
    internal static ServerRequestEnvelope ReadDecoded(
        RpcSession session,
        ReadOnlySequence<byte> decodedPayload,
        ReadOnlySequence<byte> encodedPayload,
        in ServerRequestEnvelope original,
        ProtocolV2FrameFlags flags,
        int maxMetadataBytes,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        // Caller retains both leases and the same flags/limits used for the first parse.
        // Plain requests already allocate nothing; do not add a byte comparison to that path.
        // A changed or fragmented prefix takes the original parser, including all validation.
        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0 &&
            (session.NegotiatedCapabilities & ProtocolV2Capabilities.Metadata) != 0)
        {
            var metadataOffset = ProtocolV2Constants.RequestPrefixBytes +
                ((flags & ProtocolV2FrameFlags.HasTimeBudget) != 0 ? sizeof(long) : 0);
            var prefixLength = encodedPayload.Length - original.Arguments.Length;
            if (prefixLength >= ProtocolV2Constants.RequestPrefixBytes &&
                prefixLength <= encodedPayload.FirstSpan.Length &&
                prefixLength <= decodedPayload.FirstSpan.Length &&
                encodedPayload.FirstSpan[..(int)prefixLength].SequenceEqual(
                    decodedPayload.FirstSpan[..(int)prefixLength]))
            {
                var metadataReader = new SequenceReader<byte>(encodedPayload.Slice(metadataOffset));
                if (ProtocolV2PayloadCodec.TryReadVarUInt32(ref metadataReader, out var metadataLength) &&
                    metadataLength <= maxMetadataBytes)
                {
                    return original with { Arguments = decodedPayload.Slice(prefixLength) };
                }
            }
        }
        return Read(session, decodedPayload, flags, maxMetadataBytes, timeProvider, original.RpcDeadline);
    }

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
