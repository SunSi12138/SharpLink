namespace SharpLink.Runtime;

/// <summary>Parses bounded SharpLink Protocol v2 frames.</summary>
public static class ProtocolV2FrameParser
{
    private const ProtocolV2FrameFlags KnownFlags =
        ProtocolV2FrameFlags.Error |
        ProtocolV2FrameFlags.Truncated |
        ProtocolV2FrameFlags.HasDeadline |
        ProtocolV2FrameFlags.HasMetadata |
        ProtocolV2FrameFlags.Compressed |
        ProtocolV2FrameFlags.Cancellable |
        ProtocolV2FrameFlags.OneWay |
        ProtocolV2FrameFlags.HasReturn;

    /// <summary>Attempts to consume one complete frame from a sequence.</summary>
    public static bool TryReadFrame(
        ref ReadOnlySequence<byte> buffer,
        SharpLinkProtocolOptions limits,
        out ProtocolV2FrameHeader header,
        out ReadOnlySequence<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(limits);
        header = default;
        payload = default;
        if (buffer.Length < ProtocolV2Constants.HeaderBytes)
            return false;

        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryRead(out var magic))
            return false;
        if (magic != ProtocolV2Constants.Magic)
            throw Violation(CreateInvalidMagicMessage(buffer, magic));
        if (!reader.TryReadLittleEndian(out int payloadLength))
            return false;
        if (payloadLength < 0)
            throw Violation("Frame payload length cannot be negative.");
        if (payloadLength > limits.MaxFramePayloadBytes)
        {
            throw Violation(
                $"Frame payload length {payloadLength} exceeds the configured maximum of {limits.MaxFramePayloadBytes} bytes.");
        }
        if (!reader.TryRead(out var typeRaw) || !reader.TryRead(out var flagsRaw) ||
            !reader.TryReadLittleEndian(out long requestIdBits))
        {
            return false;
        }

        var type = ParseType(typeRaw);
        var flags = ParseFlags(flagsRaw);
        var requestId = unchecked((ulong)requestIdBits);
        ValidateHeader(type, flags, requestId);
        if (reader.Remaining < payloadLength)
            return false;

        payload = buffer.Slice(ProtocolV2Constants.HeaderBytes, payloadLength);
        ValidatePayloadShape(type, flags, payload, limits);
        header = new ProtocolV2FrameHeader(type, flags, requestId);
        buffer = buffer.Slice(ProtocolV2Constants.HeaderBytes + payloadLength);
        return true;
    }

    private static ProtocolV2FrameType ParseType(byte value) => value switch
    {
        (byte)ProtocolV2FrameType.HandshakeRequest => ProtocolV2FrameType.HandshakeRequest,
        (byte)ProtocolV2FrameType.HandshakeResponse => ProtocolV2FrameType.HandshakeResponse,
        (byte)ProtocolV2FrameType.Ping => ProtocolV2FrameType.Ping,
        (byte)ProtocolV2FrameType.Pong => ProtocolV2FrameType.Pong,
        (byte)ProtocolV2FrameType.Request => ProtocolV2FrameType.Request,
        (byte)ProtocolV2FrameType.Response => ProtocolV2FrameType.Response,
        (byte)ProtocolV2FrameType.Cancel => ProtocolV2FrameType.Cancel,
        (byte)ProtocolV2FrameType.StreamData => ProtocolV2FrameType.StreamData,
        (byte)ProtocolV2FrameType.StreamComplete => ProtocolV2FrameType.StreamComplete,
        (byte)ProtocolV2FrameType.WindowUpdate => ProtocolV2FrameType.WindowUpdate,
        (byte)ProtocolV2FrameType.GoAway => ProtocolV2FrameType.GoAway,
        (byte)ProtocolV2FrameType.HealthCheck => ProtocolV2FrameType.HealthCheck,
        (byte)ProtocolV2FrameType.HealthResponse => ProtocolV2FrameType.HealthResponse,
        _ => throw Violation($"Unknown Protocol v2 frame type {value}.")
    };

    private static ProtocolV2FrameFlags ParseFlags(byte value)
    {
        var flags = (ProtocolV2FrameFlags)value;
        if ((flags & ~KnownFlags) != 0)
            throw Violation($"Unknown Protocol v2 frame flag bits 0x{value:X2}.");
        return flags;
    }

    private static void ValidateHeader(
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ulong requestId)
    {
        var controlFrame = type is ProtocolV2FrameType.HandshakeRequest or
            ProtocolV2FrameType.HandshakeResponse or
            ProtocolV2FrameType.Ping or
            ProtocolV2FrameType.Pong or
            ProtocolV2FrameType.GoAway;
        if (controlFrame && requestId != 0)
            throw Violation($"Connection-control frame {type} must use request ID 0.");
        if (!controlFrame && requestId == 0)
            throw Violation($"Frame {type} must use a non-zero request ID.");

        var allowed = type switch
        {
            ProtocolV2FrameType.HandshakeRequest => ProtocolV2FrameFlags.None,
            ProtocolV2FrameType.HandshakeResponse => ProtocolV2FrameFlags.Error | ProtocolV2FrameFlags.Truncated,
            ProtocolV2FrameType.Ping => ProtocolV2FrameFlags.None,
            ProtocolV2FrameType.Pong => ProtocolV2FrameFlags.None,
            ProtocolV2FrameType.Request => ProtocolV2FrameFlags.HasDeadline |
                                           ProtocolV2FrameFlags.HasMetadata |
                                           ProtocolV2FrameFlags.Compressed |
                                           ProtocolV2FrameFlags.Cancellable |
                                           ProtocolV2FrameFlags.OneWay |
                                           ProtocolV2FrameFlags.HasReturn,
            ProtocolV2FrameType.Response => ProtocolV2FrameFlags.Error |
                                            ProtocolV2FrameFlags.Truncated |
                                            ProtocolV2FrameFlags.Compressed,
            ProtocolV2FrameType.Cancel => ProtocolV2FrameFlags.None,
            ProtocolV2FrameType.StreamData => ProtocolV2FrameFlags.Compressed,
            ProtocolV2FrameType.StreamComplete => ProtocolV2FrameFlags.Error | ProtocolV2FrameFlags.Truncated,
            ProtocolV2FrameType.WindowUpdate => ProtocolV2FrameFlags.None,
            ProtocolV2FrameType.GoAway => ProtocolV2FrameFlags.Error | ProtocolV2FrameFlags.Truncated,
            ProtocolV2FrameType.HealthCheck => ProtocolV2FrameFlags.None,
            ProtocolV2FrameType.HealthResponse => ProtocolV2FrameFlags.None,
            _ => ProtocolV2FrameFlags.None
        };
        if ((flags & ~allowed) != 0)
            throw Violation($"Frame {type} does not allow flags {flags}.");
        if ((flags & ProtocolV2FrameFlags.Truncated) != 0 && (flags & ProtocolV2FrameFlags.Error) == 0)
            throw Violation("Truncated is valid only on an error frame.");
        if ((flags & (ProtocolV2FrameFlags.Error | ProtocolV2FrameFlags.Compressed)) ==
            (ProtocolV2FrameFlags.Error | ProtocolV2FrameFlags.Compressed))
        {
            throw Violation("Error frames cannot carry compressed business payloads.");
        }
        if (type == ProtocolV2FrameType.Request &&
            (flags & (ProtocolV2FrameFlags.OneWay | ProtocolV2FrameFlags.HasReturn)) ==
            (ProtocolV2FrameFlags.OneWay | ProtocolV2FrameFlags.HasReturn))
        {
            throw Violation("One-way requests cannot request a return payload.");
        }
    }

    private static void ValidatePayloadShape(
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        SharpLinkProtocolOptions limits)
    {
        switch (type)
        {
            case ProtocolV2FrameType.HandshakeRequest:
                if (payload.Length < 32 || payload.Length >
                    36L + limits.MaxMetadataBytes +
                    SharpLinkCompressionOptions.MaxProviders * (1 + SharpLinkCompressionToken.MaxUtf8Bytes))
                    throw Violation("HandshakeRequest payload has an invalid bounded length.");
                break;
            case ProtocolV2FrameType.HandshakeResponse:
                if ((flags & ProtocolV2FrameFlags.Error) != 0)
                    ProtocolV2PayloadCodec.ValidateErrorPayload(payload, limits.MaxErrorMessageBytes);
                else
                {
                    if (payload.Length < 23 || payload.Length > 87)
                        throw Violation("HandshakeResponse payload has an invalid length.");
                }
                break;
            case ProtocolV2FrameType.Ping:
            case ProtocolV2FrameType.Pong:
                if (payload.Length != sizeof(long))
                    throw Violation($"{type} payload must contain one 64-bit monotonic timestamp.");
                break;
            case ProtocolV2FrameType.Request:
                ValidateRequestPayload(payload, flags, limits.MaxMetadataBytes);
                break;
            case ProtocolV2FrameType.Response:
                if ((flags & ProtocolV2FrameFlags.Error) != 0)
                    ProtocolV2PayloadCodec.ValidateErrorPayload(payload, limits.MaxErrorMessageBytes);
                break;
            case ProtocolV2FrameType.Cancel:
                if (payload.Length > 1)
                    throw Violation("Cancel payload must contain zero or one reason byte.");
                break;
            case ProtocolV2FrameType.StreamData:
                if (payload.Length < sizeof(ushort))
                    throw Violation("StreamData payload is missing its UInt16 stream ID.");
                break;
            case ProtocolV2FrameType.StreamComplete:
                if ((flags & ProtocolV2FrameFlags.Error) == 0)
                {
                    if (payload.Length != sizeof(ushort))
                        throw Violation("Successful StreamComplete payload must contain only its UInt16 stream ID.");
                }
                else
                {
                    if (payload.Length < sizeof(ushort) + 3)
                        throw Violation("Error StreamComplete payload is incomplete.");
                    ProtocolV2PayloadCodec.ValidateErrorPayload(
                        payload.Slice(sizeof(ushort)), limits.MaxErrorMessageBytes);
                }
                break;
            case ProtocolV2FrameType.WindowUpdate:
                if (payload.Length != sizeof(ushort) + sizeof(uint))
                    throw Violation("WindowUpdate payload must contain UInt16 stream ID and UInt32 credit.");
                var windowReader = new SequenceReader<byte>(payload);
                if (!windowReader.TryReadLittleEndian(out short _) ||
                    !windowReader.TryReadLittleEndian(out int credit) || credit <= 0)
                {
                    throw Violation("WindowUpdate credit must be between 1 and Int32.MaxValue.");
                }
                break;
            case ProtocolV2FrameType.GoAway:
                if (payload.Length < sizeof(ulong) + 3)
                    throw Violation("GoAway payload is incomplete.");
                ProtocolV2PayloadCodec.ValidateErrorPayload(
                    payload.Slice(sizeof(ulong)), limits.MaxErrorMessageBytes);
                break;
            case ProtocolV2FrameType.HealthCheck:
                if (!payload.IsEmpty)
                    throw Violation("HealthCheck payload must be empty.");
                break;
            case ProtocolV2FrameType.HealthResponse:
                if (payload.Length != 1)
                    throw Violation("HealthResponse payload must contain exactly one status byte.");
                var healthReader = new SequenceReader<byte>(payload);
                if (!healthReader.TryRead(out var status) ||
                    status is not (byte)SharpLinkHealthStatus.Ready and
                    not (byte)SharpLinkHealthStatus.Draining and
                    not (byte)SharpLinkHealthStatus.Unhealthy)
                {
                    throw Violation($"Unknown health status {status}.");
                }
                break;
        }
    }

    private static void ValidateRequestPayload(
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        int maxMetadataBytes)
    {
        if (payload.Length < ProtocolV2Constants.RequestPrefixBytes)
            throw Violation("Request payload is shorter than its routing prefix.");
        var reader = new SequenceReader<byte>(payload);
        reader.Advance(ProtocolV2Constants.RequestPrefixBytes);
        if ((flags & ProtocolV2FrameFlags.HasDeadline) != 0 && !reader.TryReadLittleEndian(out long _))
            throw Violation("Request deadline field is truncated.");
        if ((flags & ProtocolV2FrameFlags.HasMetadata) == 0)
            return;
        if (!ProtocolV2PayloadCodec.TryReadVarUInt32(ref reader, out var metadataLength))
            throw Violation("Request metadata length is truncated or invalid.");
        if (metadataLength > maxMetadataBytes)
            throw Violation($"Request metadata exceeds its {maxMetadataBytes}-byte limit.");
        if (reader.Remaining < metadataLength)
            throw Violation("Request metadata payload is truncated.");
    }

    internal static SharpLinkException Violation(string message)
        => new(SharpLinkErrorCode.ProtocolViolation, message);

    private static string CreateInvalidMagicMessage(ReadOnlySequence<byte> buffer, byte actualMagic)
    {
        // This path is terminal for the connection. Preserve a small bounded prefix so a
        // long-running failure report can distinguish a bad writer from parser misalignment
        // without adding allocations or validation to healthy frames.
        var prefixLength = (int)Math.Min(buffer.Length, 32);
        Span<byte> prefix = stackalloc byte[prefixLength];
        buffer.Slice(0, prefixLength).CopyTo(prefix);
        return $"Invalid Protocol v2 frame magic 0x{actualMagic:X2}; " +
               $"remaining={buffer.Length}, prefix={Convert.ToHexString(prefix)}.";
    }
}

/// <summary>Writes SharpLink Protocol v2 frame headers and payload lengths.</summary>
public static class ProtocolV2FrameWriter
{
    /// <summary>Begins a frame and returns a token that must be ended after writing its payload.</summary>
    public static ProtocolV2FrameToken BeginFrame(
        IRpcByteBufferWriter writer,
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ulong requestId)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var start = writer.WrittenCount;
        var span = writer.GetSpan(ProtocolV2Constants.HeaderBytes);
        span.Clear();
        span[0] = ProtocolV2Constants.Magic;
        span[5] = (byte)type;
        span[6] = (byte)flags;
        BinaryPrimitives.WriteUInt64LittleEndian(span[7..15], requestId);
        writer.Advance(ProtocolV2Constants.HeaderBytes);
        return new ProtocolV2FrameToken(start);
    }

    /// <summary>Finishes a frame by backfilling its bounded Int32 payload length.</summary>
    public static void EndFrame(IRpcByteBufferWriter writer, ProtocolV2FrameToken token)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var length = writer.WrittenCount - token.StartOffset - ProtocolV2Constants.HeaderBytes;
        if (length < 0)
            throw new ArgumentException("Frame token does not belong to this writer.", nameof(token));
        var span = writer.WrittenSpan;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(token.StartOffset + 1, sizeof(int)), length);
    }

    /// <summary>Writes a frame with no payload.</summary>
    public static void WriteEmptyFrame(
        IRpcByteBufferWriter writer,
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ulong requestId)
    {
        var token = BeginFrame(writer, type, flags, requestId);
        EndFrame(writer, token);
    }
}

/// <summary>Identifies a Protocol v2 frame header awaiting payload length backfill.</summary>
public readonly record struct ProtocolV2FrameToken(int StartOffset);
