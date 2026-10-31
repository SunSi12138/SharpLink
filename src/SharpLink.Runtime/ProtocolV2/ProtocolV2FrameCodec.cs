using System.Diagnostics;

namespace SharpLink.Runtime;

/// <summary>Parses bounded SharpLink Protocol3 frames.</summary>
public static class ProtocolV2FrameParser
{
    private const ProtocolV2FrameFlags KnownFlags =
        ProtocolV2FrameFlags.Error |
        ProtocolV2FrameFlags.Truncated |
        ProtocolV2FrameFlags.HasTimeBudget |
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
        return TryReadFrameCore(
            ref buffer,
            limits,
            limits.MaxFramePayloadBytes,
            negotiatedLimit: false,
            out header,
            out payload);
    }

    internal static bool TryReadFrame(
        ref ReadOnlySequence<byte> buffer,
        SharpLinkProtocolOptions limits,
        int maxFramePayloadBytes,
        out ProtocolV2FrameHeader header,
        out ReadOnlySequence<byte> payload)
        => TryReadFrameCore(
            ref buffer,
            limits,
            maxFramePayloadBytes,
            negotiatedLimit: true,
            out header,
            out payload);

    private static bool TryReadFrameCore(
        ref ReadOnlySequence<byte> buffer,
        SharpLinkProtocolOptions limits,
        int maxFramePayloadBytes,
        bool negotiatedLimit,
        out ProtocolV2FrameHeader header,
        out ReadOnlySequence<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Debug.Assert(maxFramePayloadBytes > 0);
        Debug.Assert(maxFramePayloadBytes <= limits.MaxFramePayloadBytes);
        header = default;
        payload = default;
        if (buffer.Length < ProtocolV2Constants.HeaderBytes)
            return false;

        byte magic, typeRaw, flagsRaw;
        int payloadLength;
        long requestIdBits;
        var first = buffer.FirstSpan;
        if (first.Length >= ProtocolV2Constants.HeaderBytes)
        {
            // Only the fixed header must be contiguous; payload may cross segments.
            magic = first[0];
            payloadLength = BinaryPrimitives.ReadInt32LittleEndian(first[1..5]);
            typeRaw = first[5];
            flagsRaw = first[6];
            requestIdBits = BinaryPrimitives.ReadInt64LittleEndian(first[7..15]);
        }
        else
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryRead(out magic) ||
                !reader.TryReadLittleEndian(out payloadLength) ||
                !reader.TryRead(out typeRaw) || !reader.TryRead(out flagsRaw) ||
                !reader.TryReadLittleEndian(out requestIdBits))
            {
                return false;
            }
        }

        if (magic != ProtocolV2Constants.Magic)
            throw Violation(ProtocolViolationReason.InvalidMagic, CreateInvalidMagicMessage(buffer, magic));
        if (payloadLength < 0)
            throw Violation("Frame payload length cannot be negative.");
        if (payloadLength > maxFramePayloadBytes)
        {
            var limitKind = negotiatedLimit ? "negotiated" : "configured";
            throw Violation(
                $"Frame payload length {payloadLength} exceeds the {limitKind} maximum of {maxFramePayloadBytes} bytes.");
        }

        var type = ParseType(typeRaw);
        var flags = ParseFlags(flagsRaw);
        var requestId = unchecked((ulong)requestIdBits);
        ValidateHeader(type, flags, requestId);
        if (buffer.Length - ProtocolV2Constants.HeaderBytes < payloadLength)
            return false;

        payload = buffer.Slice(ProtocolV2Constants.HeaderBytes, payloadLength);
        ValidatePayloadShape(type, flags, payload, limits, maxFramePayloadBytes);
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
        (byte)ProtocolV2FrameType.ContractManifest => ProtocolV2FrameType.ContractManifest,
        (byte)ProtocolV2FrameType.ResponseCompressionPreferenceUpdate => ProtocolV2FrameType.ResponseCompressionPreferenceUpdate,
        (byte)ProtocolV2FrameType.ResponseCompressionPreferenceAck => ProtocolV2FrameType.ResponseCompressionPreferenceAck,
        (byte)ProtocolV2FrameType.SessionRefreshRequested => ProtocolV2FrameType.SessionRefreshRequested,
        (byte)ProtocolV2FrameType.BindContract => ProtocolV2FrameType.BindContract,
        (byte)ProtocolV2FrameType.BindResult => ProtocolV2FrameType.BindResult,
        (byte)ProtocolV2FrameType.BindingInvalidated => ProtocolV2FrameType.BindingInvalidated,
        (byte)ProtocolV2FrameType.ReleaseBinding => ProtocolV2FrameType.ReleaseBinding,
        (byte)ProtocolV2FrameType.CancelBind => ProtocolV2FrameType.CancelBind,
        (byte)ProtocolV2FrameType.RequestAccepted => ProtocolV2FrameType.RequestAccepted,
        (byte)ProtocolV2FrameType.OneWayStreamRequest => ProtocolV2FrameType.OneWayStreamRequest,
        _ => throw Violation($"Unknown Protocol3 frame type {value}.")
    };

    private static ProtocolV2FrameFlags ParseFlags(byte value)
    {
        var flags = (ProtocolV2FrameFlags)value;
        if ((flags & ~KnownFlags) != 0)
            throw Violation($"Unknown Protocol3 frame flag bits 0x{value:X2}.");
        return flags;
    }

    internal static void ValidateHeader(
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ulong requestId)
    {
        if ((byte)type > (byte)ProtocolV2FrameType.OneWayStreamRequest)
            throw Violation("Unknown Protocol3 frame type.");
        var controlFrame = type is ProtocolV2FrameType.HandshakeRequest or
            ProtocolV2FrameType.HandshakeResponse or
            ProtocolV2FrameType.Ping or
            ProtocolV2FrameType.Pong or
            ProtocolV2FrameType.GoAway or
            ProtocolV2FrameType.ContractManifest or
            ProtocolV2FrameType.ResponseCompressionPreferenceUpdate or
            ProtocolV2FrameType.ResponseCompressionPreferenceAck or
            ProtocolV2FrameType.SessionRefreshRequested or
            ProtocolV2FrameType.BindingInvalidated or
            ProtocolV2FrameType.ReleaseBinding;
        if (controlFrame && requestId != 0)
            throw Violation($"Connection-control frame {type} must use request ID 0.");
        if (!controlFrame && (requestId == 0 || requestId > long.MaxValue))
            throw Violation($"Frame {type} must use a positive 63-bit correlation ID.");

        var allowed = type switch
        {
            ProtocolV2FrameType.HandshakeRequest => ProtocolV2FrameFlags.None,
            ProtocolV2FrameType.HandshakeResponse => ProtocolV2FrameFlags.Error | ProtocolV2FrameFlags.Truncated,
            ProtocolV2FrameType.Ping => ProtocolV2FrameFlags.None,
            ProtocolV2FrameType.Pong => ProtocolV2FrameFlags.None,
            ProtocolV2FrameType.Request or ProtocolV2FrameType.OneWayStreamRequest => ProtocolV2FrameFlags.HasTimeBudget |
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
            ProtocolV2FrameType.ContractManifest => ProtocolV2FrameFlags.None,
            ProtocolV2FrameType.ResponseCompressionPreferenceUpdate => ProtocolV2FrameFlags.None,
            ProtocolV2FrameType.ResponseCompressionPreferenceAck => ProtocolV2FrameFlags.None,
            ProtocolV2FrameType.SessionRefreshRequested => ProtocolV2FrameFlags.None,
            _ => ProtocolV2FrameFlags.None
        };
        if (type == ProtocolV2FrameType.OneWayStreamRequest && (flags & ProtocolV2FrameFlags.OneWay) == 0)
            throw Violation("OneWayStreamRequest requires the OneWay flag.");
        if (type == ProtocolV2FrameType.GoAway && (flags & ProtocolV2FrameFlags.Error) == 0)
            throw Violation("GoAway requires its binary error envelope flag.");
        if ((flags & ~allowed) != 0)
            throw Violation($"Frame {type} does not allow flags {flags}.");
        if ((flags & ProtocolV2FrameFlags.Truncated) != 0 && (flags & ProtocolV2FrameFlags.Error) == 0)
            throw Violation("Truncated is valid only on an error frame.");
        if ((flags & (ProtocolV2FrameFlags.Error | ProtocolV2FrameFlags.Compressed)) ==
            (ProtocolV2FrameFlags.Error | ProtocolV2FrameFlags.Compressed))
        {
            throw Violation("Error frames cannot carry compressed business payloads.");
        }
        if ((type is ProtocolV2FrameType.Request or ProtocolV2FrameType.OneWayStreamRequest) &&
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
        SharpLinkProtocolOptions limits,
        int maxFramePayloadBytes)
    {
        switch (type)
        {
            case ProtocolV2FrameType.HandshakeRequest:
                _ = ProtocolV2PayloadCodec.ReadHandshakeRequest(payload, limits);
                break;
            case ProtocolV2FrameType.HandshakeResponse:
                if ((flags & ProtocolV2FrameFlags.Error) != 0)
                    ProtocolV2PayloadCodec.ValidateErrorPayload(payload, limits.MaxErrorMessageBytes);
                else
                {
                    _ = ProtocolV2PayloadCodec.ReadHandshakeResponse(payload, limits);
                }
                break;
            case ProtocolV2FrameType.Ping:
            case ProtocolV2FrameType.Pong:
                if (payload.Length != sizeof(long))
                    throw Violation($"{type} payload must contain one 64-bit monotonic timestamp.");
                break;
            case ProtocolV2FrameType.Request:
            case ProtocolV2FrameType.OneWayStreamRequest:
                ValidateRequestPayload(payload, type, flags, limits.MaxMetadataBytes, maxFramePayloadBytes);
                break;
            case ProtocolV2FrameType.Response:
                if ((flags & ProtocolV2FrameFlags.Error) != 0)
                    ProtocolV2PayloadCodec.ValidateErrorPayload(payload, limits.MaxErrorMessageBytes);
                else if ((flags & ProtocolV2FrameFlags.Compressed) != 0)
                    ValidateCompressedBody(payload, 0, maxFramePayloadBytes);
                break;
            case ProtocolV2FrameType.Cancel:
                _ = ProtocolV2PayloadCodec.ReadCancelReason(payload);
                break;
            case ProtocolV2FrameType.StreamData:
                if (payload.Length < sizeof(ushort))
                    throw Violation("StreamData payload is missing its UInt16 stream ID.");
                if ((flags & ProtocolV2FrameFlags.Compressed) != 0)
                    ValidateCompressedBody(payload.Slice(sizeof(ushort)), sizeof(ushort), maxFramePayloadBytes);
                break;
            case ProtocolV2FrameType.StreamComplete:
                if ((flags & ProtocolV2FrameFlags.Error) == 0)
                {
                    if (payload.Length != sizeof(ushort))
                        throw Violation("Successful StreamComplete payload must contain only its UInt16 stream ID.");
                }
                else
                {
                    if (payload.Length < sizeof(ushort) + 5)
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
                if (payload.Length < sizeof(ulong) + 5)
                    throw Violation("GoAway payload is incomplete.");
                var goAwayReader = new SequenceReader<byte>(payload);
                if (!goAwayReader.TryReadLittleEndian(out long lastAcceptedId) || lastAcceptedId < 0)
                    throw Violation("GoAway last accepted ID is outside the positive 63-bit domain.");
                ProtocolV2PayloadCodec.ValidateErrorPayload(
                    payload.Slice(sizeof(ulong)), limits.MaxErrorMessageBytes);
                break;
            case ProtocolV2FrameType.BindContract:
                _ = Protocol3BindingControlCodec.ReadBindContract(payload);
                break;
            case ProtocolV2FrameType.BindResult:
                _ = Protocol3BindingControlCodec.ReadBindResult(payload, Protocol3BindingLimits.Maximum.Slots);
                break;
            case ProtocolV2FrameType.BindingInvalidated:
                _ = Protocol3BindingControlCodec.ReadBindingInvalidated(payload, Protocol3BindingLimits.Maximum.Slots);
                break;
            case ProtocolV2FrameType.ReleaseBinding:
                _ = Protocol3BindingControlCodec.ReadReleaseBinding(payload, Protocol3BindingLimits.Maximum.Slots);
                break;
            case ProtocolV2FrameType.CancelBind:
            case ProtocolV2FrameType.RequestAccepted:
                if (!payload.IsEmpty)
                    throw Violation($"{type} payload must be empty.");
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
            case ProtocolV2FrameType.ContractManifest:
                ProtocolV2ContractManifestCodec.ValidatePayloadShape(payload, limits);
                break;
            case ProtocolV2FrameType.ResponseCompressionPreferenceUpdate:
                _ = ProtocolV2PayloadCodec.ReadResponseCompressionPreferenceUpdate(payload);
                break;
            case ProtocolV2FrameType.ResponseCompressionPreferenceAck:
                _ = ProtocolV2PayloadCodec.ReadResponseCompressionPreferenceAck(payload);
                break;
            case ProtocolV2FrameType.SessionRefreshRequested:
                _ = ProtocolV2PayloadCodec.ReadSessionRefreshRequested(payload);
                break;
        }
    }

    private static void ValidateRequestPayload(
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        int maxMetadataBytes,
        int maxFramePayloadBytes)
    {
        var prefixBytes = type == ProtocolV2FrameType.OneWayStreamRequest
            ? ProtocolV2Constants.OneWayStreamRequestPrefixBytes
            : ProtocolV2Constants.RequestPrefixBytes;
        if (payload.Length < prefixBytes)
            throw Violation("Request payload is shorter than its routing prefix.");
        var reader = new SequenceReader<byte>(payload);
        reader.Advance(ProtocolV2Constants.RequestPrefixBytes);
        if (type == ProtocolV2FrameType.OneWayStreamRequest)
        {
            if (!reader.TryReadLittleEndian(out short streamCount) || streamCount is < 1 or > 127)
                throw Violation("OneWayStreamRequest client stream count is invalid.");
        }
        if ((flags & ProtocolV2FrameFlags.HasTimeBudget) != 0)
        {
            if (!reader.TryReadLittleEndian(out long timeBudget) || timeBudget < 0)
                throw Violation("Request time budget is truncated or negative.");
        }
        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0)
        {
            if (!ProtocolV2PayloadCodec.TryReadVarUInt32(ref reader, out var metadataLength))
                throw Violation("Request metadata length is truncated or invalid.");
            if (metadataLength > maxMetadataBytes || reader.Remaining < metadataLength)
                throw Violation("Request metadata exceeds its bounded payload or limit.");
            ProtocolV2PayloadCodec.ValidateMetadataPayload(reader.Sequence.Slice(reader.Position, metadataLength));
            reader.Advance(metadataLength);
        }
        if ((flags & ProtocolV2FrameFlags.Compressed) != 0)
            ValidateCompressedBody(reader.Sequence.Slice(reader.Position), checked((int)reader.Consumed), maxFramePayloadBytes);
    }

    private static void ValidateCompressedBody(ReadOnlySequence<byte> body, int prefixBytes, int maxFramePayloadBytes)
    {
        var reader = new SequenceReader<byte>(body);
        if (!reader.TryReadLittleEndian(out int originalLengthBits))
            throw Violation("Compressed business payload is missing its original length.");
        var originalLength = unchecked((uint)originalLengthBits);
        if ((ulong)originalLength + (uint)prefixBytes > (uint)maxFramePayloadBytes)
            throw Violation("Decompressed business payload would exceed the negotiated frame limit.");
        // Structural validation only: the selected provider is never called before admission.
    }

    internal static SharpLinkException Violation(string message)
        => new SharpLinkProtocolViolationException(ProtocolViolationReason.MalformedFrame, message);

    internal static SharpLinkException Violation(ProtocolViolationReason reason, string message)
        => new SharpLinkProtocolViolationException(reason, message);

    private static string CreateInvalidMagicMessage(ReadOnlySequence<byte> buffer, byte actualMagic)
        // Never capture attacker-controlled payload bytes in diagnostics, including debug builds.
        => $"Invalid Protocol3 frame magic 0x{actualMagic:X2}; remaining={buffer.Length}.";

}

/// <summary>Writes SharpLink Protocol3 frame headers and payload lengths.</summary>
internal static class ProtocolV2FrameWriter
{
    /// <summary>Begins a frame and returns a token that must be ended after writing its payload.</summary>
    public static ProtocolV2FrameToken BeginFrame(
        IRpcByteBufferWriter writer,
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ulong requestId)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ProtocolV2FrameParser.ValidateHeader(type, flags, requestId);
        var start = writer.WrittenCount;
        var span = writer.GetSpan(ProtocolV2Constants.HeaderBytes)[..ProtocolV2Constants.HeaderBytes];
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
        if (token.StartOffset < 0 || token.StartOffset > writer.WrittenCount - ProtocolV2Constants.HeaderBytes)
            throw new ArgumentException("Frame token does not belong to this writer.", nameof(token));
        var length = checked(writer.WrittenCount - token.StartOffset - ProtocolV2Constants.HeaderBytes);
        if (length > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(writer), "Frame exceeds the absolute protocol payload limit.");
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
        if (flags != ProtocolV2FrameFlags.None || type is not
            (ProtocolV2FrameType.Response or ProtocolV2FrameType.HealthCheck or
             ProtocolV2FrameType.CancelBind or ProtocolV2FrameType.RequestAccepted))
            throw new ArgumentException("This Protocol3 frame requires a nonempty payload.", nameof(type));
        var token = BeginFrame(writer, type, flags, requestId);
        EndFrame(writer, token);
    }
}

/// <summary>Identifies a Protocol3 frame header awaiting payload length backfill.</summary>
internal readonly record struct ProtocolV2FrameToken(int StartOffset);
