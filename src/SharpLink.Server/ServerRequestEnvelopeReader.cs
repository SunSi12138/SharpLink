namespace SharpLink.Server;

internal static class ServerRequestEnvelopeReader
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

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

    internal static void ValidateMetadataSyntax(
        RpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        int maxMetadataBytes,
        TimeProvider timeProvider)
    {
        if ((flags & ProtocolV2FrameFlags.HasMetadata) == 0)
            return;

        ArgumentNullException.ThrowIfNull(timeProvider);
        var reader = new SequenceReader<byte>(payload);
        _ = ReadRoutingPrefix(ref reader, flags, timeProvider);
        var metadataLength = ReadMetadataLength(session, ref reader, maxMetadataBytes);
        ValidateMetadataPayload(reader.Sequence.Slice(reader.Position, metadataLength));
    }

    private static void ValidateMetadataPayload(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!ProtocolV2PayloadCodec.TryReadVarUInt32(ref reader, out var countBits) ||
            countBits > int.MaxValue)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.MalformedFrame,
                "Request metadata entry count is invalid.");
        }

        var count = checked((int)countBits);
        if (count > reader.Remaining / 3)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.MalformedFrame,
                "Request metadata entry count exceeds its bounded payload.");
        }

        var decoder = StrictUtf8.GetDecoder();
        Span<char> characters = stackalloc char[256];
        for (var index = 0; index < count; index++)
        {
            ValidateMetadataUtf8(
                ref reader,
                "key",
                decoder,
                characters,
                requireNonWhitespace: true);
            ValidateMetadataUtf8(
                ref reader,
                "value",
                decoder,
                characters,
                requireNonWhitespace: false);
        }

        if (reader.Remaining != 0)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.MalformedFrame,
                "Request metadata has trailing bytes.");
        }
    }

    private static void ValidateMetadataUtf8(
        ref SequenceReader<byte> reader,
        string field,
        Decoder decoder,
        scoped Span<char> characters,
        bool requireNonWhitespace)
    {
        if (!ProtocolV2PayloadCodec.TryReadVarUInt32(ref reader, out var lengthBits) ||
            lengthBits > int.MaxValue)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.MalformedFrame,
                $"Request metadata {field} length is invalid.");
        }

        var length = checked((int)lengthBits);
        if (reader.Remaining < length)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.MalformedFrame,
                $"Request metadata {field} is truncated.");
        }

        var bytes = reader.Sequence.Slice(reader.Position, length);
        reader.Advance(length);
        var hasNonWhitespace = false;
        try
        {
            decoder.Reset();
            foreach (var segment in bytes)
            {
                var remaining = segment.Span;
                while (!remaining.IsEmpty)
                {
                    decoder.Convert(
                        remaining,
                        characters,
                        flush: false,
                        out var bytesUsed,
                        out var charsUsed,
                        out _);
                    for (var index = 0; index < charsUsed; index++)
                        hasNonWhitespace |= !char.IsWhiteSpace(characters[index]);
                    remaining = remaining[bytesUsed..];
                }
            }

            decoder.Convert(
                ReadOnlySpan<byte>.Empty,
                characters,
                flush: true,
                out _,
                out var finalCharsUsed,
                out _);
            for (var index = 0; index < finalCharsUsed; index++)
                hasNonWhitespace |= !char.IsWhiteSpace(characters[index]);
        }
        catch (DecoderFallbackException)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.MalformedFrame,
                $"Request metadata {field} is not valid UTF-8.");
        }

        if (requireNonWhitespace && !hasNonWhitespace)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.MalformedFrame,
                "Request metadata key cannot be empty.");
        }
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
