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
        if (originalLength == 0 || originalLength < RuntimeContext.Compression.MinimumPayloadBytes)
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
            SharpLinkCompressionResult result;
            try
            {
                result = provider.Compress(
                    payload.Slice(prefixLength),
                    candidate,
                    maxCompressedBytes,
                    cancellationToken);
            }
            catch (SharpLinkCompressionOutputLimitException)
            {
                RuntimeContext.Buffers.Return(candidate);
                return packet;
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

            var actualWritten = candidate.WrittenCount - compressedStart;
            if (result.ConsumedBytes != originalLength || result.WrittenBytes != actualWritten)
            {
                throw new SharpLinkCompressionProviderException(
                    SharpLinkErrorCode.Internal,
                    $"Compression provider '{compressionProfile}' reported inconsistent consumed or written bytes.");
            }
            if (!RuntimeContext.Compression.IsBeneficial(
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
        if ((flags & ProtocolV2FrameFlags.Compressed) == 0)
            return payload;

        var protocolState = Volatile.Read(ref _protocolState);
        // Envelope validation already has to locate the business prefix. Reuse that result for
        // decompression instead of walking Request metadata/deadline fields a second time.
        var prefixLength = ValidateInboundPayloadEnvelope(protocolState, type, flags, payload);

        var compressionBinding = protocolState.Options?.CompressionBinding;
        var provider = compressionBinding?.Provider;
        if (provider is null)
            throw ProtocolV2FrameParser.Violation("A compressed frame has no negotiated provider.");
        var compressionProfile = compressionBinding?.WireProfile;

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
            SharpLinkCompressionResult result;
            try
            {
                result = provider.Decompress(
                    compressedBody,
                    owner,
                    originalLength,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is InvalidDataException or EndOfStreamException or SharpLinkCompressionOutputLimitException)
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
            if (result.ConsumedBytes != compressedBody.Length ||
                result.WrittenBytes != actualWritten ||
                actualWritten != originalLength)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.DataLoss,
                    "Compressed payload is truncated, contains trailing data, or does not match its declared original length.");
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
        if ((flags & ProtocolV2FrameFlags.Compressed) == 0)
            return;
        _ = ValidateInboundPayloadEnvelope(Volatile.Read(ref _protocolState), type, flags, payload);
    }

    private static int ValidateInboundPayloadEnvelope(
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
        if (payload.Length < prefixLength + sizeof(uint) + 1L)
            throw ProtocolV2FrameParser.Violation("Compressed payload is missing its original length or body.");
        var reader = new SequenceReader<byte>(payload.Slice(prefixLength));
        if (!reader.TryReadLittleEndian(out int originalLengthBits))
            throw ProtocolV2FrameParser.Violation("Compressed payload original length is truncated.");
        var originalLength = unchecked((uint)originalLengthBits);
        if (originalLength == 0 || originalLength > int.MaxValue)
            throw ProtocolV2FrameParser.Violation("Compressed payload original length is outside the supported range.");
        if ((long)prefixLength + originalLength > options.MaxFramePayloadBytes)
        {
            throw ProtocolV2FrameParser.Violation(
                "Compressed payload original length exceeds the negotiated frame limit.");
        }
        return prefixLength;
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
        var reader = new SequenceReader<byte>(payload.Slice(prefixLength));
        if (!reader.TryReadLittleEndian(out int originalLengthBits))
            throw ProtocolV2FrameParser.Violation("Compressed payload original length is truncated.");
        return checked((int)unchecked((uint)originalLengthBits));
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
        if (type != ProtocolV2FrameType.Request)
            return -1;

        var reader = new SequenceReader<byte>(payload);
        if (reader.Remaining < ProtocolV2Constants.RequestPrefixBytes)
            throw ProtocolV2FrameParser.Violation("Request routing prefix is truncated.");
        reader.Advance(ProtocolV2Constants.RequestPrefixBytes);
        if ((flags & ProtocolV2FrameFlags.HasDeadline) != 0)
        {
            if (reader.Remaining < sizeof(long))
                throw ProtocolV2FrameParser.Violation("Request deadline is truncated.");
            reader.Advance(sizeof(long));
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
