using System.Text;

namespace SharpLink.Runtime;

/// <summary>Encodes and decodes Protocol v2 control and error payloads.</summary>
public static class ProtocolV2PayloadCodec
{
    private const ProtocolV2Capabilities RecognizedCapabilities =
        RpcSessionProtocolRules.RecognizedCapabilities;
    private static readonly Encoding SStrictUtf8 = new UTF8Encoding(false, true);
    private const int HandshakeRequestFixedBytes =
        sizeof(ushort) + sizeof(ulong) + sizeof(ulong) + sizeof(int) + sizeof(int) + sizeof(int);
    private const int HandshakeResponseBytes =
        sizeof(ushort) + sizeof(ulong) + sizeof(int) + sizeof(int) + sizeof(int);

    /// <summary>Writes a bounded handshake request payload.</summary>
    public static void WriteHandshakeRequest(
        IBufferWriter<byte> writer,
        in ProtocolV2HandshakeRequest request,
        SharpLinkProtocolOptions limits)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(limits);
        if ((request.RequiredCapabilities & ~request.SupportedCapabilities) != 0)
        {
            throw new ArgumentException(
                "Required handshake capabilities must also be advertised as supported.",
                nameof(request));
        }
        ValidateLocalLimits(request.MaxFramePayloadBytes, request.StreamReceiveWindowBytes,
            request.ConnectionReceiveWindowBytes);
        if (request.AuthenticationPayload.Length > limits.MaxMetadataBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(request),
                $"Authentication payload exceeds the {limits.MaxMetadataBytes}-byte handshake limit.");
        }

        ValidateCompressionProfiles(request.CompressionProfiles.Span);

        WriteUInt16(writer, request.MinorVersion);
        WriteUInt64(writer, (ulong)request.SupportedCapabilities);
        WriteUInt64(writer, (ulong)request.RequiredCapabilities);
        WriteInt32(writer, request.MaxFramePayloadBytes);
        WriteInt32(writer, request.StreamReceiveWindowBytes);
        WriteInt32(writer, request.ConnectionReceiveWindowBytes);
        WriteCompressionProfiles(writer, request.CompressionProfiles.Span);
        WriteVarUInt32(writer, checked((uint)request.AuthenticationPayload.Length));
        writer.Write(request.AuthenticationPayload.Span);
    }

    /// <summary>Reads and validates a complete handshake request payload.</summary>
    public static ProtocolV2HandshakeRequest ReadHandshakeRequest(
        ReadOnlySequence<byte> payload,
        SharpLinkProtocolOptions limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (payload.Length < HandshakeRequestFixedBytes + 2)
            throw ProtocolV2FrameParser.Violation("HandshakeRequest payload is incomplete.");
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short minorBits) ||
            !reader.TryReadLittleEndian(out long supportedBits) ||
            !reader.TryReadLittleEndian(out long requiredBits) ||
            !reader.TryReadLittleEndian(out int maxFrame) ||
            !reader.TryReadLittleEndian(out int streamWindow) ||
            !reader.TryReadLittleEndian(out int connectionWindow))
        {
            throw ProtocolV2FrameParser.Violation("HandshakeRequest payload is truncated.");
        }
        var compressionProfiles = ReadCompressionProfiles(ref reader);
        if (!TryReadVarUInt32(ref reader, out var authLength))
            throw ProtocolV2FrameParser.Violation("Handshake authentication payload length is truncated.");
        ValidatePeerLimits(maxFrame, streamWindow, connectionWindow);
        if (authLength > limits.MaxMetadataBytes)
            throw ProtocolV2FrameParser.Violation($"Authentication payload exceeds {limits.MaxMetadataBytes} bytes.");
        if (reader.Remaining != authLength)
            throw ProtocolV2FrameParser.Violation("Handshake authentication payload length does not match the frame.");

        var auth = authLength == 0
            ? ReadOnlyMemory<byte>.Empty
            : reader.Sequence.Slice(reader.Position, authLength).ToArray();
        var supportedCapabilities = (ProtocolV2Capabilities)unchecked((ulong)supportedBits);
        var requiredCapabilities = (ProtocolV2Capabilities)unchecked((ulong)requiredBits);
        if ((requiredCapabilities & ~supportedCapabilities) != 0)
        {
            throw ProtocolV2FrameParser.Violation(
                "Required handshake capabilities were not included in the supported capability set.");
        }
        return new ProtocolV2HandshakeRequest(
            unchecked((ushort)minorBits),
            supportedCapabilities,
            requiredCapabilities,
            maxFrame,
            streamWindow,
            connectionWindow,
            auth,
            compressionProfiles);
    }

    /// <summary>Writes a negotiated handshake response payload.</summary>
    public static void WriteHandshakeResponse(
        IBufferWriter<byte> writer,
        in ProtocolV2HandshakeResponse response)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ValidateRecognizedCapabilities(response.NegotiatedCapabilities, nameof(response));
        ValidateOutboundCompressionSelection(response);
        ValidateLocalLimits(response.MaxFramePayloadBytes, response.StreamReceiveWindowBytes,
            response.ConnectionReceiveWindowBytes);
        WriteUInt16(writer, response.MinorVersion);
        WriteUInt64(writer, (ulong)response.NegotiatedCapabilities);
        WriteInt32(writer, response.MaxFramePayloadBytes);
        WriteInt32(writer, response.StreamReceiveWindowBytes);
        WriteInt32(writer, response.ConnectionReceiveWindowBytes);
        if (response.CompressionProfile is null)
        {
            WriteByte(writer, 0);
        }
        else
        {
            SharpLinkCompressionProfile.Validate(response.CompressionProfile, nameof(response));
            WriteByte(writer, checked((byte)response.CompressionProfile.Length));
            WriteAscii(writer, response.CompressionProfile);
        }
    }

    /// <summary>Reads a negotiated handshake response payload.</summary>
    public static ProtocolV2HandshakeResponse ReadHandshakeResponse(
        ReadOnlySequence<byte> payload,
        SharpLinkProtocolOptions limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (payload.Length < HandshakeResponseBytes + 1 ||
            payload.Length > HandshakeResponseBytes + 1 + SharpLinkCompressionProfile.MaxAsciiBytes)
            throw ProtocolV2FrameParser.Violation("HandshakeResponse payload has an invalid length.");
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short minorBits) ||
            !reader.TryReadLittleEndian(out long capabilitiesBits) ||
            !reader.TryReadLittleEndian(out int maxFrame) ||
            !reader.TryReadLittleEndian(out int streamWindow) ||
            !reader.TryReadLittleEndian(out int connectionWindow) ||
            !reader.TryRead(out var profileLength))
        {
            throw ProtocolV2FrameParser.Violation("HandshakeResponse payload is truncated.");
        }
        if (reader.Remaining != profileLength)
            throw ProtocolV2FrameParser.Violation("HandshakeResponse compression token length does not match the frame.");
        var profile = profileLength == 0 ? null : ReadCompressionProfile(ref reader, profileLength);
        ValidatePeerLimits(maxFrame, streamWindow, connectionWindow);
        var negotiatedCapabilities = (ProtocolV2Capabilities)unchecked((ulong)capabilitiesBits);
        if ((negotiatedCapabilities & ~RecognizedCapabilities) != 0)
            throw ProtocolV2FrameParser.Violation("HandshakeResponse negotiated unknown capabilities.");
        var response = new ProtocolV2HandshakeResponse(
            unchecked((ushort)minorBits),
            negotiatedCapabilities,
            maxFrame,
            streamWindow,
            connectionWindow,
            profile);
        ValidateInboundCompressionSelection(response);
        return response;
    }

    private static void ValidateRecognizedCapabilities(
        ProtocolV2Capabilities capabilities,
        string parameterName)
    {
        if ((capabilities & ~RecognizedCapabilities) != 0)
            throw new ArgumentOutOfRangeException(parameterName, "Handshake capabilities contain unknown bits.");
    }

    private static void ValidateOutboundCompressionSelection(
        in ProtocolV2HandshakeResponse response)
    {
        var negotiated =
            (response.NegotiatedCapabilities & ProtocolV2Capabilities.Compression) != 0;
        if (negotiated == (response.CompressionProfile is not null))
            return;
        throw new ArgumentException(
            "Negotiated compression and its selected profile must either both be present or both be absent.",
            nameof(response));
    }

    private static void ValidateInboundCompressionSelection(
        in ProtocolV2HandshakeResponse response)
    {
        var negotiated =
            (response.NegotiatedCapabilities & ProtocolV2Capabilities.Compression) != 0;
        if (negotiated == (response.CompressionProfile is not null))
            return;
        throw ProtocolV2FrameParser.Violation(
            "HandshakeResponse compression capability and selected profile are inconsistent.");
    }

    private static void ValidateCompressionProfiles(ReadOnlySpan<string> profiles)
    {
        if (profiles.Length > SharpLinkCompressionOptions.MaxProviders)
            throw new ArgumentOutOfRangeException(nameof(profiles));
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            SharpLinkCompressionProfile.Validate(profile, nameof(profiles));
            if (!unique.Add(profile))
                throw new ArgumentException($"Compression wire profile '{profile}' is advertised more than once.", nameof(profiles));
        }
    }

    private static void WriteCompressionProfiles(IBufferWriter<byte> writer, ReadOnlySpan<string> profiles)
    {
        WriteByte(writer, checked((byte)profiles.Length));
        foreach (var profile in profiles)
        {
            WriteByte(writer, checked((byte)profile.Length));
            WriteAscii(writer, profile);
        }
    }

    private static ReadOnlyMemory<string> ReadCompressionProfiles(ref SequenceReader<byte> reader)
    {
        if (!reader.TryRead(out var count) || count > SharpLinkCompressionOptions.MaxProviders)
            throw ProtocolV2FrameParser.Violation("Handshake compression profile count is invalid.");
        if (count == 0)
            return ReadOnlyMemory<string>.Empty;

        var profiles = new string[count];
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryRead(out var length) || length == 0 ||
                length > SharpLinkCompressionProfile.MaxAsciiBytes || reader.Remaining < length)
            {
                throw ProtocolV2FrameParser.Violation("Handshake compression profile is truncated or invalid.");
            }
            var profile = ReadCompressionProfile(ref reader, length);
            if (!unique.Add(profile))
                throw ProtocolV2FrameParser.Violation($"Compression wire profile '{profile}' is advertised more than once.");
            profiles[index] = profile;
        }
        return profiles;
    }

    private static string ReadCompressionProfile(ref SequenceReader<byte> reader, int length)
    {
        Span<byte> bytes = stackalloc byte[length];
        if (!reader.TryCopyTo(bytes))
            throw ProtocolV2FrameParser.Violation("Handshake compression profile is truncated.");
        foreach (var value in bytes)
        {
            if (value is < 0x21 or > 0x7e)
                throw ProtocolV2FrameParser.Violation("Handshake compression profile is not canonical ASCII.");
        }
        reader.Advance(length);
        return Encoding.ASCII.GetString(bytes);
    }

    private static void WriteAscii(IBufferWriter<byte> writer, string value)
    {
        var span = writer.GetSpan(value.Length);
        for (var index = 0; index < value.Length; index++)
            span[index] = checked((byte)value[index]);
        writer.Advance(value.Length);
    }

    private static void WriteByte(IBufferWriter<byte> writer, byte value)
    {
        var span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }

    /// <summary>Writes a non-zero stream credit update.</summary>
    public static void WriteWindowUpdate(
        IBufferWriter<byte> writer,
        in ProtocolV2WindowUpdate update)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (update.Credit == 0 || update.Credit > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(update), "Window credit must be between 1 and Int32.MaxValue.");
        WriteUInt16(writer, update.StreamId);
        var span = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(span, update.Credit);
        writer.Advance(sizeof(uint));
    }

    /// <summary>Reads one complete stream credit update.</summary>
    public static ProtocolV2WindowUpdate ReadWindowUpdate(ReadOnlySequence<byte> payload)
    {
        if (payload.Length != sizeof(ushort) + sizeof(uint))
            throw ProtocolV2FrameParser.Violation("WindowUpdate payload must contain UInt16 stream ID and UInt32 credit.");
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short streamIdBits) ||
            !reader.TryReadLittleEndian(out int creditBits) || creditBits <= 0)
        {
            throw ProtocolV2FrameParser.Violation("WindowUpdate credit must be between 1 and Int32.MaxValue.");
        }
        return new ProtocolV2WindowUpdate(
            unchecked((ushort)streamIdBits),
            checked((uint)creditBits));
    }

    /// <summary>Writes one validated cancellation reason byte.</summary>
    public static void WriteCancelReason(
        IBufferWriter<byte> writer,
        ProtocolV2CancelReason reason)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (reason is not ProtocolV2CancelReason.Unspecified and
            not ProtocolV2CancelReason.UserCancellation and
            not ProtocolV2CancelReason.DeadlineExceeded and
            not ProtocolV2CancelReason.ConsumerAbandoned)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
        var span = writer.GetSpan(1);
        span[0] = (byte)reason;
        writer.Advance(1);
    }

    /// <summary>Reads one complete cancellation reason payload.</summary>
    public static ProtocolV2CancelReason ReadCancelReason(ReadOnlySequence<byte> payload)
    {
        if (payload.Length != 1)
            throw ProtocolV2FrameParser.Violation("Cancel payload must contain exactly one reason byte.");
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryRead(out var reasonBits))
            throw ProtocolV2FrameParser.Violation("Cancel reason is truncated.");
        var reason = (ProtocolV2CancelReason)reasonBits;
        ValidateCancelReason(reason);
        return reason;
    }

    /// <summary>Writes one fixed-width protocol health response.</summary>
    public static void WriteHealthResponse(
        IBufferWriter<byte> writer,
        SharpLinkHealthStatus status)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (status is not SharpLinkHealthStatus.Ready and
            not SharpLinkHealthStatus.Draining and
            not SharpLinkHealthStatus.Unhealthy)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        var span = writer.GetSpan(1);
        span[0] = (byte)status;
        writer.Advance(1);
    }

    /// <summary>Reads one complete fixed-width protocol health response.</summary>
    public static SharpLinkHealthCheckResult ReadHealthResponse(ReadOnlySequence<byte> payload)
    {
        if (payload.Length != 1)
            throw ProtocolV2FrameParser.Violation("HealthResponse payload must contain exactly one status byte.");
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryRead(out var statusBits))
            throw ProtocolV2FrameParser.Violation("HealthResponse status is truncated.");
        var status = (SharpLinkHealthStatus)statusBits;
        ValidateHealthStatus(status);
        return new SharpLinkHealthCheckResult(status);
    }

    /// <summary>Writes an error payload without a finer-grained detail code.</summary>
    public static void WriteError(
        IBufferWriter<byte> writer,
        SharpLinkErrorCode code,
        string? message,
        int maxMessageBytes,
        out bool truncated)
        => WriteError(
            writer,
            code,
            SharpLinkErrorDetails.Unspecified,
            message,
            maxMessageBytes,
            out truncated);

    /// <summary>Writes a binary error payload and reports whether the UTF-8 message was truncated.</summary>
    public static void WriteError(
        IBufferWriter<byte> writer,
        SharpLinkErrorCode code,
        ushort detailCode,
        string? message,
        int maxMessageBytes,
        out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessageBytes);
        if (!IsDefinedErrorCode(code))
            throw new ArgumentOutOfRangeException(nameof(code), "Error code must be a defined SharpLinkErrorCode value.");
        message ??= string.Empty;
        var charCount = message.Length;
        var byteCount = Encoding.UTF8.GetByteCount(message);
        truncated = byteCount > maxMessageBytes;
        if (truncated)
        {
            charCount = FindUtf8PrefixLength(message, maxMessageBytes);
            byteCount = Encoding.UTF8.GetByteCount(message.AsSpan(0, charCount));
        }

        WriteUInt16(writer, checked((ushort)code));
        WriteUInt16(writer, detailCode);
        WriteVarUInt32(writer, checked((uint)byteCount));
        if (byteCount == 0)
            return;
        var destination = writer.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(message.AsSpan(0, charCount), destination);
        writer.Advance(written);
    }

    /// <summary>Reads a complete binary error payload.</summary>
    public static ProtocolV2Error ReadError(
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        int maxMessageBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessageBytes);
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short codeBits) ||
            !reader.TryReadLittleEndian(out short detailBits) ||
            !TryReadVarUInt32(ref reader, out var messageLength))
        {
            throw ProtocolV2FrameParser.Violation("Binary error payload is truncated.");
        }
        if (messageLength > maxMessageBytes)
            throw ProtocolV2FrameParser.Violation($"Error message exceeds {maxMessageBytes} bytes.");
        if (reader.Remaining != messageLength)
            throw ProtocolV2FrameParser.Violation("Binary error message length does not match the frame.");

        var code = (SharpLinkErrorCode)unchecked((ushort)codeBits);
        if (!IsDefinedErrorCode(code))
            throw ProtocolV2FrameParser.Violation($"Unknown error code {unchecked((ushort)codeBits)}.");
        var detailCode = unchecked((ushort)detailBits);
        var message = messageLength == 0
            ? string.Empty
            : DecodeStrictUtf8(reader.Sequence.Slice(reader.Position, messageLength), "Binary error message");
        return new ProtocolV2Error(
            code,
            detailCode,
            message,
            (flags & ProtocolV2FrameFlags.Truncated) != 0);
    }

    internal static void ValidateErrorPayload(ReadOnlySequence<byte> payload, int maxMessageBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessageBytes);
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short codeBits) ||
            !reader.TryReadLittleEndian(out short _) ||
            !TryReadVarUInt32(ref reader, out var messageLength))
        {
            throw ProtocolV2FrameParser.Violation("Binary error payload is truncated.");
        }
        if (messageLength > maxMessageBytes)
            throw ProtocolV2FrameParser.Violation($"Error message exceeds {maxMessageBytes} bytes.");
        if (reader.Remaining != messageLength)
            throw ProtocolV2FrameParser.Violation("Binary error message length does not match the frame.");
        if (!IsDefinedErrorCode((SharpLinkErrorCode)unchecked((ushort)codeBits)))
            throw ProtocolV2FrameParser.Violation($"Unknown error code {unchecked((ushort)codeBits)}.");
        ValidateStrictUtf8(reader.Sequence.Slice(reader.Position, messageLength), "Binary error message");
    }

    internal static int GetMetadataPayloadLength(SharpLinkMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var length = GetVarUInt32Length(checked((uint)metadata.Count));
        for (var index = 0; index < metadata.Count; index++)
        {
            var entry = metadata[index];
            var keyBytes = SStrictUtf8.GetByteCount(entry.Key);
            var valueBytes = SStrictUtf8.GetByteCount(entry.Value);
            length = checked(length + GetVarUInt32Length(checked((uint)keyBytes)) + keyBytes);
            length = checked(length + GetVarUInt32Length(checked((uint)valueBytes)) + valueBytes);
        }
        return length;
    }

    internal static void WriteMetadata(IBufferWriter<byte> writer, SharpLinkMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(metadata);
        WriteVarUInt32(writer, checked((uint)metadata.Count));
        for (var index = 0; index < metadata.Count; index++)
        {
            var entry = metadata[index];
            WriteUtf8(writer, entry.Key);
            WriteUtf8(writer, entry.Value);
        }
    }

    internal static SharpLinkMetadata ReadMetadata(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!TryReadVarUInt32(ref reader, out var countBits) || countBits > int.MaxValue)
            throw ProtocolV2FrameParser.Violation("Request metadata entry count is invalid.");
        var count = checked((int)countBits);
        if (count > reader.Remaining / 3)
            throw ProtocolV2FrameParser.Violation("Request metadata entry count exceeds its bounded payload.");
        var entries = new KeyValuePair<string, string>[count];
        for (var index = 0; index < count; index++)
        {
            var key = ReadUtf8(ref reader, "key");
            if (string.IsNullOrWhiteSpace(key))
                throw ProtocolV2FrameParser.Violation("Request metadata key cannot be empty.");
            var value = ReadUtf8(ref reader, "value");
            entries[index] = new KeyValuePair<string, string>(key, value);
        }
        if (reader.Remaining != 0)
            throw ProtocolV2FrameParser.Violation("Request metadata has trailing bytes.");
        return SharpLinkMetadata.FromValidatedEntries(entries);
    }

    internal static void WriteVarUInt32(IBufferWriter<byte> writer, uint value)
    {
        var span = writer.GetSpan(5);
        var index = 0;
        while (value >= 0x80)
        {
            span[index++] = (byte)(value | 0x80);
            value >>= 7;
        }
        span[index++] = (byte)value;
        writer.Advance(index);
    }

    internal static bool TryReadVarUInt32(ref SequenceReader<byte> reader, out uint value)
    {
        value = 0;
        for (var index = 0; index < 5; index++)
        {
            if (!reader.TryRead(out var current))
                return false;
            if (index == 4 && (current & 0xF0) != 0)
                return false;
            value |= (uint)(current & 0x7F) << (index * 7);
            if ((current & 0x80) == 0)
                return index == 0 || current != 0;
        }
        return false;
    }

    private static string DecodeStrictUtf8(ReadOnlySequence<byte> bytes, string field)
    {
        try
        {
            return SStrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw Violation($"{field} is not valid UTF-8.", exception);
        }
    }

    private static void ValidateStrictUtf8(ReadOnlySequence<byte> bytes, string field)
    {
        try
        {
            var decoder = SStrictUtf8.GetDecoder();
            Span<char> characters = stackalloc char[256];
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
                        out _,
                        out _);
                    remaining = remaining[bytesUsed..];
                }
            }
            decoder.Convert(
                ReadOnlySpan<byte>.Empty,
                characters,
                flush: true,
                out _,
                out _,
                out _);
        }
        catch (DecoderFallbackException exception)
        {
            throw Violation($"{field} is not valid UTF-8.", exception);
        }
    }

    private static int GetVarUInt32Length(uint value)
    {
        var length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }
        return length;
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, string value)
    {
        var byteCount = SStrictUtf8.GetByteCount(value);
        WriteVarUInt32(writer, checked((uint)byteCount));
        if (byteCount == 0)
            return;
        var destination = writer.GetSpan(byteCount);
        var written = SStrictUtf8.GetBytes(value.AsSpan(), destination);
        writer.Advance(written);
    }

    private static string ReadUtf8(ref SequenceReader<byte> reader, string field)
    {
        if (!TryReadVarUInt32(ref reader, out var lengthBits) || lengthBits > int.MaxValue)
            throw ProtocolV2FrameParser.Violation($"Request metadata {field} length is invalid.");
        var length = checked((int)lengthBits);
        if (reader.Remaining < length)
            throw ProtocolV2FrameParser.Violation($"Request metadata {field} is truncated.");
        try
        {
            var value = length == 0
                ? string.Empty
                : SStrictUtf8.GetString(reader.Sequence.Slice(reader.Position, length));
            reader.Advance(length);
            return value;
        }
        catch (DecoderFallbackException exception)
        {
            throw Violation($"Request metadata {field} is not valid UTF-8.", exception);
        }
    }

    private static int FindUtf8PrefixLength(string value, int maxBytes)
    {
        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var candidate = low + ((high - low + 1) >> 1);
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, candidate)) <= maxBytes)
                low = candidate;
            else
                high = candidate - 1;
        }
        if (low < value.Length && low > 0 &&
            char.IsHighSurrogate(value[low - 1]) && char.IsLowSurrogate(value[low]))
        {
            low--;
        }
        while (low > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, low)) > maxBytes)
            low--;
        return low;
    }

    private static void ValidatePeerLimits(int maxFrame, int streamWindow, int connectionWindow)
    {
        if (maxFrame < SharpLinkProtocolOptions.MinMaxFramePayloadBytes ||
            maxFrame > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes)
        {
            throw ProtocolV2FrameParser.Violation(
                $"Peer frame limit must be between {SharpLinkProtocolOptions.MinMaxFramePayloadBytes} and {SharpLinkProtocolOptions.MaxMaxFramePayloadBytes} bytes.");
        }
        if (streamWindow <= 0 || connectionWindow <= 0 || connectionWindow < streamWindow)
            throw ProtocolV2FrameParser.Violation("Peer receive windows are invalid.");
    }

    private static void ValidateLocalLimits(int maxFrame, int streamWindow, int connectionWindow)
    {
        if (maxFrame < SharpLinkProtocolOptions.MinMaxFramePayloadBytes ||
            maxFrame > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFrame),
                $"Frame limit must be between {SharpLinkProtocolOptions.MinMaxFramePayloadBytes} and {SharpLinkProtocolOptions.MaxMaxFramePayloadBytes} bytes.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamWindow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connectionWindow);
        if (connectionWindow < streamWindow)
            throw new ArgumentException("Connection receive window cannot be smaller than stream receive window.");
    }

    private static void ValidateHealthStatus(SharpLinkHealthStatus status)
    {
        if (status is not SharpLinkHealthStatus.Ready and
            not SharpLinkHealthStatus.Draining and
            not SharpLinkHealthStatus.Unhealthy)
        {
            throw ProtocolV2FrameParser.Violation($"Unknown health status {(byte)status}.");
        }
    }

    private static void ValidateCancelReason(ProtocolV2CancelReason reason)
    {
        if (reason is not ProtocolV2CancelReason.Unspecified and
            not ProtocolV2CancelReason.UserCancellation and
            not ProtocolV2CancelReason.DeadlineExceeded and
            not ProtocolV2CancelReason.ConsumerAbandoned)
        {
            throw ProtocolV2FrameParser.Violation($"Unknown Cancel reason {(byte)reason}.");
        }
    }

    private static SharpLinkException Violation(string message, Exception? innerException = null)
        => new SharpLinkProtocolViolationException(
            ProtocolViolationReason.MalformedFrame,
            message,
            innerException);

    internal static bool IsDefinedErrorCode(SharpLinkErrorCode code) => code switch
    {
        SharpLinkErrorCode.RemoteError or
        SharpLinkErrorCode.AuthenticationRejected or
        SharpLinkErrorCode.AuthenticationExpired or
        SharpLinkErrorCode.AuthorizationDenied or
        SharpLinkErrorCode.ConnectionClosed or
        SharpLinkErrorCode.HeartbeatTimeout or
        SharpLinkErrorCode.ProtocolViolation or
        SharpLinkErrorCode.DataLoss or
        SharpLinkErrorCode.ResourceExhausted or
        SharpLinkErrorCode.Unavailable or
        SharpLinkErrorCode.Cancelled or
        SharpLinkErrorCode.InvalidArgument or
        SharpLinkErrorCode.DeadlineExceeded or
        SharpLinkErrorCode.NotFound or
        SharpLinkErrorCode.AlreadyExists or
        SharpLinkErrorCode.PermissionDenied or
        SharpLinkErrorCode.FailedPrecondition or
        SharpLinkErrorCode.Aborted or
        SharpLinkErrorCode.OutOfRange or
        SharpLinkErrorCode.Unimplemented or
        SharpLinkErrorCode.Internal => true,
        _ => false
    };

    private static void WriteUInt16(IBufferWriter<byte> writer, ushort value)
    {
        var span = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        writer.Advance(sizeof(ushort));
    }

    private static void WriteUInt64(IBufferWriter<byte> writer, ulong value)
    {
        var span = writer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        writer.Advance(sizeof(ulong));
    }

    private static void WriteInt32(IBufferWriter<byte> writer, int value)
    {
        var span = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        writer.Advance(sizeof(int));
    }
}
