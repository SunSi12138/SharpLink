namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal string? CompressionProfile
        => Volatile.Read(ref _protocolState).Options?.CompressionBinding?.WireProfile;

    private IRpcByteBufferWriter PrepareOutboundPacket(
        IRpcByteBufferWriter packet,
        CancellationToken cancellationToken)
    {
        var protocolState = Volatile.Read(ref _protocolState);
        var compressionBinding = protocolState.Options?.CompressionBinding;
        var provider = compressionBinding?.Provider;
        if (provider is null)
            return packet;
        var compressionProfile = compressionBinding?.WireProfile;
        var maxFramePayloadBytes = protocolState.Options!.MaxFramePayloadBytes;

        var written = packet.WrittenSpan;
        if (written.Length < ProtocolV2Constants.HeaderBytes)
            return packet;
        var type = (ProtocolV2FrameType)written[5];
        var flags = (ProtocolV2FrameFlags)written[6];
        if ((flags & (ProtocolV2FrameFlags.Compressed | ProtocolV2FrameFlags.Error)) != 0)
            return packet;

        var payloadMemory = packet.WrittenMemory[ProtocolV2Constants.HeaderBytes..];
        var payload = new ReadOnlySequence<byte>(payloadMemory);
        ValidateNegotiatedRequestPrefix(protocolState.Options!, type, flags, payload);
        var prefixLength = GetBusinessPrefixLength(type, flags, payload);
        if (prefixLength < 0)
            return packet;
        var originalLength = checked((int)payload.Length - prefixLength);
        if ((long)prefixLength + originalLength > maxFramePayloadBytes)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                $"Outbound frame payload exceeds the negotiated {maxFramePayloadBytes}-byte limit.");
        }
        var policy = _compressionSendPolicyState.Current;
        if (!policy.Enabled)
            return packet;
        if (Role == RpcSessionRole.Server &&
            !Volatile.Read(ref _appliedResponseCompressionPreference).Allowed)
        {
            return packet;
        }
        if (originalLength == 0 || originalLength < policy.MinimumPayloadBytes)
            return packet;

        var candidate = RuntimeContext.Buffers.Rent(
            checked(ProtocolV2Constants.HeaderBytes + maxFramePayloadBytes));
        try
        {
            candidate.Write(packet.WrittenSpan[..(ProtocolV2Constants.HeaderBytes + prefixLength)]);
            candidate.WrittenSpan[6] |= (byte)ProtocolV2FrameFlags.Compressed;
            var originalLengthSpan = candidate.GetSpan(sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(originalLengthSpan, checked((uint)originalLength));
            candidate.Advance(sizeof(uint));

            var compressedStart = candidate.WrittenCount;
            var maxCompressedBytes = maxFramePayloadBytes - prefixLength - sizeof(uint);
            bool compressed;
            try
            {
                compressed = provider.TryCompress(
                    payload.Slice(prefixLength),
                    candidate,
                    maxCompressedBytes,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new SharpLinkCompressionProviderException(
                    SharpLinkErrorCode.Internal,
                    $"Compression provider '{compressionProfile}' failed before the frame was queued.",
                    exception);
            }

            if (!compressed)
            {
                RuntimeContext.Buffers.Return(candidate);
                return packet;
            }

            var actualWritten = candidate.WrittenCount - compressedStart;
            if (!policy.IsBeneficial(
                    originalLength,
                    checked(actualWritten + sizeof(uint))))
            {
                RuntimeContext.Buffers.Return(candidate);
                return packet;
            }

            var candidatePayloadLength = candidate.WrittenCount - ProtocolV2Constants.HeaderBytes;
            BinaryPrimitives.WriteInt32LittleEndian(
                candidate.WrittenSpan.Slice(1, sizeof(int)),
                candidatePayloadLength);
            RuntimeContext.Buffers.Return(packet);
            return candidate;
        }
        catch
        {
            RuntimeContext.Buffers.Return(candidate);
            throw;
        }
    }

    internal ReadOnlySequence<byte> DecodeInboundPayload(
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        CancellationToken cancellationToken,
        out IRpcByteBufferWriter? owner)
    {
        owner = null;
        var protocolState = Volatile.Read(ref _protocolState);
        if (protocolState.Options is { } options)
            ValidateNegotiatedRequestPrefix(options, type, flags, payload);
        if ((flags & ProtocolV2FrameFlags.Compressed) == 0)
            return payload;

        ValidateInboundPayloadEnvelope(protocolState, type, flags, payload);

        var compressionBinding = protocolState.Options?.CompressionBinding;
        var provider = compressionBinding?.Provider;
        if (provider is null)
            throw ProtocolV2FrameParser.Violation("A compressed frame has no negotiated provider.");
        var compressionProfile = compressionBinding?.WireProfile;

        var prefixLength = GetBusinessPrefixLength(type, flags, payload);

        var compressedEnvelope = payload.Slice(prefixLength);
        var lengthReader = new SequenceReader<byte>(compressedEnvelope);
        if (!lengthReader.TryReadLittleEndian(out int originalLengthBits))
            throw ProtocolV2FrameParser.Violation("Compressed payload original length is truncated.");
        var originalLengthUnsigned = unchecked((uint)originalLengthBits);
        var originalLength = checked((int)originalLengthUnsigned);

        var compressedBody = compressedEnvelope.Slice(sizeof(uint));
        owner = RuntimeContext.Buffers.Rent(checked(prefixLength + originalLength));
        try
        {
            if (prefixLength != 0)
            {
                foreach (var segment in payload.Slice(0, prefixLength))
                    owner.Write(segment.Span);
            }
            var outputStart = owner.WrittenCount;
            try
            {
                provider.Decompress(
                    compressedBody,
                    owner,
                    originalLength,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.DataLoss,
                    $"Compressed payload for '{compressionProfile}' is truncated, corrupt, or exceeds its declared length.",
                    exception);
            }
            catch (Exception exception)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.Internal,
                    $"Compression provider '{compressionProfile}' failed while decoding a frame.",
                    exception);
            }

            var actualWritten = owner.WrittenCount - outputStart;
            if (actualWritten != originalLength)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.DataLoss,
                    "Compressed payload does not match its declared original length.");
            }
            return new ReadOnlySequence<byte>(owner.WrittenMemory);
        }
        catch
        {
            RuntimeContext.Buffers.Return(owner);
            owner = null;
            throw;
        }
    }

    internal void ValidateInboundPayloadEnvelope(
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload)
    {
        var protocolState = Volatile.Read(ref _protocolState);
        if (protocolState.Options is { } options)
            ValidateNegotiatedRequestPrefix(options, type, flags, payload);
        if ((flags & ProtocolV2FrameFlags.Compressed) == 0)
            return;
        ValidateInboundPayloadEnvelope(protocolState, type, flags, payload);
    }

    private static void ValidateInboundPayloadEnvelope(
        RpcSessionProtocolState protocolState,
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload)
    {
        var options = protocolState.Options;
        if (options?.CompressionBinding?.Provider is null ||
            (options.Capabilities & ProtocolV2Capabilities.Compression) == 0)
        {
            throw ProtocolV2FrameParser.Violation(
                "A compressed frame was received without negotiated compression.");
        }

        var prefixLength = GetBusinessPrefixLength(type, flags, payload);
        if (prefixLength < 0)
            throw ProtocolV2FrameParser.Violation($"Frame {type} cannot carry compressed payload data.");
        if (payload.Length < prefixLength + sizeof(uint))
            throw ProtocolV2FrameParser.Violation("Compressed payload is missing its original length.");
        var reader = new SequenceReader<byte>(payload.Slice(prefixLength));
        if (!reader.TryReadLittleEndian(out int originalLengthBits))
            throw ProtocolV2FrameParser.Violation("Compressed payload original length is truncated.");
        var originalLength = unchecked((uint)originalLengthBits);
        if (originalLength > int.MaxValue)
            throw ProtocolV2FrameParser.Violation("Compressed payload original length is outside the supported range.");
        if ((long)prefixLength + originalLength > options.MaxFramePayloadBytes)
        {
            throw ProtocolV2FrameParser.Violation(
                "Compressed payload original length exceeds the negotiated frame limit.");
        }
    }

    private static void ValidateNegotiatedRequestPrefix(
        NegotiatedSessionOptions options,
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload)
    {
        if (type is not ProtocolV2FrameType.Request and not ProtocolV2FrameType.OneWayStreamRequest)
            return;
        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0 &&
            (options.Capabilities & ProtocolV2Capabilities.Metadata) == 0)
            throw ProtocolV2FrameParser.Violation("Request metadata was not negotiated.");
        if (type == ProtocolV2FrameType.OneWayStreamRequest)
        {
            if (payload.Length < ProtocolV2Constants.OneWayStreamRequestPrefixBytes)
                throw ProtocolV2FrameParser.Violation("OneWayStreamRequest routing prefix is truncated.");
            var reader = new SequenceReader<byte>(payload.Slice(ProtocolV2Constants.RequestPrefixBytes));
            if (!reader.TryReadLittleEndian(out short count) || count <= 0 ||
                count > options.Authority.Limits.MaxClientStreamsPerCall)
                throw ProtocolV2FrameParser.Violation("OneWayStreamRequest exceeds the negotiated client stream count.");
        }
    }

    internal void ReturnDecodedPayload(IRpcByteBufferWriter? owner)
    {
        if (owner is not null)
            RuntimeContext.Buffers.Return(owner);
    }

    internal static ushort ReadCompressedStreamId(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short streamIdBits))
            throw ProtocolV2FrameParser.Violation("Compressed StreamData stream ID is truncated.");
        return unchecked((ushort)streamIdBits);
    }

    internal static int ReadCompressedOriginalLength(
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload)
    {
        var prefixLength = GetBusinessPrefixLength(type, flags, payload);
        if (prefixLength < 0)
            throw ProtocolV2FrameParser.Violation("Frame cannot carry compressed business data.");
        var reader = new SequenceReader<byte>(payload.Slice(prefixLength));
        if (!reader.TryReadLittleEndian(out int originalLengthBits) || originalLengthBits < 0)
            throw ProtocolV2FrameParser.Violation("Compressed payload original length is truncated or out of range.");
        return originalLengthBits;
    }

    internal static int ReadCompressedDecodedPayloadLength(
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload)
    {
        var prefixLength = GetBusinessPrefixLength(type, flags, payload);
        var originalLength = ReadCompressedOriginalLength(type, flags, payload);
        if ((long)prefixLength + originalLength > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes)
            throw ProtocolV2FrameParser.Violation("Decoded payload exceeds the absolute frame limit.");
        return prefixLength + originalLength;
    }

    private static int GetBusinessPrefixLength(
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload)
    {
        if (type == ProtocolV2FrameType.Response)
            return 0;
        if (type == ProtocolV2FrameType.StreamData)
        {
            if (payload.Length < sizeof(ushort))
                throw ProtocolV2FrameParser.Violation("StreamData stream ID is truncated.");
            return sizeof(ushort);
        }
        if (type is not ProtocolV2FrameType.Request and not ProtocolV2FrameType.OneWayStreamRequest)
            return -1;

        var reader = new SequenceReader<byte>(payload);
        if (reader.Remaining < ProtocolV2Constants.RequestPrefixBytes)
            throw ProtocolV2FrameParser.Violation("Request routing prefix is truncated.");
        reader.Advance(ProtocolV2Constants.RequestPrefixBytes);
        if (type == ProtocolV2FrameType.OneWayStreamRequest)
        {
            if ((flags & ProtocolV2FrameFlags.OneWay) == 0 || (flags & ProtocolV2FrameFlags.HasReturn) != 0 ||
                !reader.TryReadLittleEndian(out short streamCount) || streamCount is < 1 or > 127)
                throw ProtocolV2FrameParser.Violation("OneWayStreamRequest prefix or flags are invalid.");
        }
        if ((flags & ProtocolV2FrameFlags.HasTimeBudget) != 0)
        {
            if (!reader.TryReadLittleEndian(out long budget) || budget < 0)
                throw ProtocolV2FrameParser.Violation("Request time budget is truncated or negative.");
        }
        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0)
        {
            if (!ProtocolV2PayloadCodec.TryReadVarUInt32(ref reader, out var metadataLength) ||
                reader.Remaining < metadataLength)
            {
                throw ProtocolV2FrameParser.Violation("Request metadata length is invalid.");
            }
            reader.Advance(metadataLength);
        }
        return checked((int)(payload.Length - reader.Remaining));
    }
}

internal sealed class SharpLinkCompressionProviderException : SharpLinkException
{
    internal SharpLinkCompressionProviderException(
        SharpLinkErrorCode code,
        string message,
        Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}
