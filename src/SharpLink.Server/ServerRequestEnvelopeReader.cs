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
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (flags != original.Flags || original.PrefixBytes < original.FixedPrefixBytes ||
            encodedPayload.Length < original.PrefixBytes || decodedPayload.Length < original.PrefixBytes)
            throw ProtocolV2FrameParser.Violation("Decoded request prefix does not match its admitted envelope.");
        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0)
        {
            if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.Metadata) == 0)
                throw ProtocolV2FrameParser.Violation("Request metadata was not negotiated.");
            var metadataOffset = original.FixedPrefixBytes +
                ((flags & ProtocolV2FrameFlags.HasTimeBudget) != 0 ? sizeof(long) : 0);
            var metadataReader = new SequenceReader<byte>(encodedPayload.Slice(metadataOffset));
            if (!ProtocolV2PayloadCodec.TryReadVarUInt32(ref metadataReader, out var metadataLength) ||
                metadataLength > maxMetadataBytes || metadataReader.Remaining < metadataLength)
                throw ProtocolV2FrameParser.Violation("Request metadata length is invalid.");
        }
        if (!PrefixEquals(encodedPayload.Slice(0, original.PrefixBytes), decodedPayload.Slice(0, original.PrefixBytes)))
            throw ProtocolV2FrameParser.Violation("Decompression changed an admitted request prefix.");
        // Preserve the captured binding, method, deadline, metadata, count and wire type exactly.
        // Only the arguments move to the decoded owner. No registry or method-facts lookup occurs.
        return original with { Arguments = decodedPayload.Slice(original.PrefixBytes) };
    }

    private static bool PrefixEquals(ReadOnlySequence<byte> left, ReadOnlySequence<byte> right)
    {
        if (left.IsSingleSegment && right.IsSingleSegment)
            return left.FirstSpan.SequenceEqual(right.FirstSpan);
        var leftReader = new SequenceReader<byte>(left);
        var rightReader = new SequenceReader<byte>(right);
        while (leftReader.TryRead(out var value))
            if (!rightReader.TryRead(out var other) || value != other)
                return false;
        return rightReader.End;
    }

    internal static ServerRequestEnvelope Read(
        RpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        int maxMetadataBytes,
        TimeProvider timeProvider,
        RpcDeadline resolvedDeadline = default,
        ProtocolV2FrameType frameType = ProtocolV2FrameType.Request)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (frameType is not ProtocolV2FrameType.Request and not ProtocolV2FrameType.OneWayStreamRequest)
            throw ProtocolV2FrameParser.Violation("A business request envelope requires an explicit request frame type.");
        ProtocolV2FrameParser.ValidateHeader(frameType, flags, 1);
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long interfaceHash) ||
            !reader.TryReadLittleEndian(out long methodHash))
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.MalformedFrame,
                "Request routing prefix is truncated.");
        }

        ushort clientStreamCount = 0;
        if (frameType == ProtocolV2FrameType.OneWayStreamRequest)
        {
            if (!reader.TryReadLittleEndian(out short count) || count is < 1 or > 127 ||
                count > session.BindingAuthority.Limits.MaxClientStreamsPerCall)
                throw ProtocolV2FrameParser.Violation("One-way request stream count exceeds the negotiated domain.");
            clientStreamCount = (ushort)count;
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
            metadata,
            frameType,
            flags,
            clientStreamCount,
            checked((int)reader.Consumed));
    }
}

internal readonly record struct ServerRequestEnvelope(
    long InterfaceHash,
    long MethodHash,
    ReadOnlySequence<byte> Arguments,
    RpcDeadline RpcDeadline,
    SharpLinkMetadata? Metadata,
    ProtocolV2FrameType FrameType,
    ProtocolV2FrameFlags Flags,
    ushort ClientStreamCount,
    int PrefixBytes)
{
    internal ulong BindingId => unchecked((ulong)InterfaceHash);
    internal int FixedPrefixBytes => FrameType == ProtocolV2FrameType.OneWayStreamRequest
        ? ProtocolV2Constants.OneWayStreamRequestPrefixBytes
        : ProtocolV2Constants.RequestPrefixBytes;
}
