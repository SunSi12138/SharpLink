using System.Buffers.Binary;
using System.Collections.Generic;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Protocol;

public class ProtocolV2Tests
{
    private static readonly SharpLinkProtocolOptions Limits = new();

    [Test]
    public void WriterAndParserShouldRoundTripFrame()
    {
        var writer = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer, ProtocolV2FrameType.Request, ProtocolV2FrameFlags.HasReturn, ulong.MaxValue);
        writer.Write(new byte[ProtocolV2Constants.RequestPrefixBytes + 3]);
        ProtocolV2FrameWriter.EndFrame(writer, token);

        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out var header, out var payload), "frame parsed");
        Ensure(header.Type == ProtocolV2FrameType.Request, "frame type");
        Ensure(header.Flags == ProtocolV2FrameFlags.HasReturn, "frame flags");
        Ensure(header.RequestId == ulong.MaxValue, "unsigned request ID");
        Ensure(payload.Length == ProtocolV2Constants.RequestPrefixBytes + 3, "payload length");
        Ensure(sequence.IsEmpty, "frame consumed");
    }

    [Test]
    public void ZeroThroughFourteenHeaderBytesShouldRemainPartial()
    {
        var writer = new PooledByteBufferWriter();
        ProtocolV2FrameWriter.WriteEmptyFrame(writer, ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 1);
        for (var length = 0; length < ProtocolV2Constants.HeaderBytes; length++)
        {
            var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory[..length]);
            Ensure(!ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out _, out _), $"partial header {length}");
        }
    }

    [Test]
    public void HeaderWithEveryByteInSeparateSegmentShouldParse()
    {
        var writer = new PooledByteBufferWriter();
        ProtocolV2FrameWriter.WriteEmptyFrame(writer, ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 9);
        var sequence = CreateSegmented(writer.WrittenMemory.ToArray(), 1);
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out var header, out _), "segmented header");
        Ensure(header.RequestId == 9, "segmented request ID");
    }

    [Test]
    public async Task InvalidLengthTypeFlagsAndRequestIdShouldBeRejected()
    {
        var invalidMagic = MutateHeader();
        invalidMagic[0] = 0x88;
        await ExpectProtocolViolation(invalidMagic);
        await ExpectProtocolViolation(MutateHeader(length: -1));
        await ExpectProtocolViolation(MutateHeader(length: Limits.MaxFramePayloadBytes + 1));
        await ExpectProtocolViolation(MutateHeader(type: 0xFF));
        await ExpectProtocolViolation(MutateHeader(flags: (byte)ProtocolV2FrameFlags.Error));

        var controlWithRequestId = CreateFrame(ProtocolV2FrameType.Ping, ProtocolV2FrameFlags.None, 1, new byte[8]);
        await ExpectProtocolViolation(controlWithRequestId);
        var requestWithZeroId = CreateFrame(
            ProtocolV2FrameType.Request,
            ProtocolV2FrameFlags.None,
            0,
            new byte[ProtocolV2Constants.RequestPrefixBytes]);
        await ExpectProtocolViolation(requestWithZeroId);
        await ExpectProtocolViolation(CreateFrame(
            ProtocolV2FrameType.Request,
            ProtocolV2FrameFlags.None,
            1,
            new byte[ProtocolV2Constants.RequestPrefixBytes - 1]));
    }

    [Test]
    public void CompleteHeaderWithPartialPayloadShouldRemainBuffered()
    {
        var frame = CreateFrame(
            ProtocolV2FrameType.Request,
            ProtocolV2FrameFlags.None,
            1,
            new byte[ProtocolV2Constants.RequestPrefixBytes]);
        var sequence = new ReadOnlySequence<byte>(frame.AsMemory(0, ProtocolV2Constants.HeaderBytes + 7));
        Ensure(!ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out _, out _), "partial payload");
        Ensure(sequence.Length == ProtocolV2Constants.HeaderBytes + 7, "partial payload not consumed");
    }

    [Test]
    public void HandshakeRequestAndResponseShouldRoundTrip()
    {
        var requestPayload = new PooledByteBufferWriter();
        var request = new ProtocolV2HandshakeRequest(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.Metadata | ProtocolV2Capabilities.FlowControl,
            ProtocolV2Capabilities.FlowControl,
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            new byte[] { 1, 2, 3, 4 });
        ProtocolV2PayloadCodec.WriteHandshakeRequest(requestPayload, request, Limits);
        var decodedRequest = ProtocolV2PayloadCodec.ReadHandshakeRequest(
            CreateSegmented(requestPayload.WrittenMemory.ToArray(), 2), Limits);
        Ensure(decodedRequest.MinorVersion == request.MinorVersion, "handshake minor");
        Ensure(decodedRequest.SupportedCapabilities == request.SupportedCapabilities, "handshake supported capabilities");
        Ensure(decodedRequest.RequiredCapabilities == request.RequiredCapabilities, "handshake required capabilities");
        Ensure(decodedRequest.MaxFramePayloadBytes == request.MaxFramePayloadBytes, "handshake frame limit");
        Ensure(decodedRequest.StreamReceiveWindowBytes == request.StreamReceiveWindowBytes, "handshake stream window");
        Ensure(decodedRequest.ConnectionReceiveWindowBytes == request.ConnectionReceiveWindowBytes, "handshake connection window");
        Ensure(decodedRequest.AuthenticationPayload.Span.SequenceEqual(request.AuthenticationPayload.Span), "handshake auth payload");

        var responsePayload = new PooledByteBufferWriter();
        var response = new ProtocolV2HandshakeResponse(
            0,
            ProtocolV2Capabilities.FlowControl,
            1024 * 1024,
            512 * 1024,
            8 * 1024 * 1024);
        ProtocolV2PayloadCodec.WriteHandshakeResponse(responsePayload, response);
        var decodedResponse = ProtocolV2PayloadCodec.ReadHandshakeResponse(
            new ReadOnlySequence<byte>(responsePayload.WrittenMemory), Limits);
        Ensure(decodedResponse == response, "handshake response round-trip");
    }

    [Test]
    public void BinaryErrorShouldRoundTripAndTruncateOnUtf8Boundary()
    {
        var payload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteError(
            payload,
            SharpLinkErrorCode.ResourceExhausted,
            "容量不足🙂🙂🙂",
            12,
            out var truncated);
        Ensure(truncated, "error should be truncated");

        var error = ProtocolV2PayloadCodec.ReadError(
            CreateSegmented(payload.WrittenMemory.ToArray(), 1),
            ProtocolV2FrameFlags.Error | ProtocolV2FrameFlags.Truncated,
            12);
        Ensure(error.Code == SharpLinkErrorCode.ResourceExhausted, "error code");
        Ensure(error.IsTruncated, "truncated flag");
        Ensure(System.Text.Encoding.UTF8.GetByteCount(error.Message) <= 12, "bounded UTF-8 message");
    }

    [Test]
    public async Task RequestMetadataMustBeBoundedBeforeSlice()
    {
        var payload = new PooledByteBufferWriter();
        payload.Write(new byte[ProtocolV2Constants.RequestPrefixBytes]);
        ProtocolV2PayloadCodec.WriteVarUInt32(payload, checked((uint)Limits.MaxMetadataBytes + 1));
        var frame = CreateFrame(
            ProtocolV2FrameType.Request,
            ProtocolV2FrameFlags.HasMetadata,
            1,
            payload.WrittenMemory.ToArray());
        await ExpectProtocolViolation(frame);

        var errorPayload = new PooledByteBufferWriter();
        errorPayload.Write(new byte[sizeof(ushort)]);
        ProtocolV2PayloadCodec.WriteVarUInt32(
            errorPayload, checked((uint)Limits.MaxErrorMessageBytes + 1));
        await ExpectProtocolViolation(CreateFrame(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.Error,
            1,
            errorPayload.WrittenMemory.ToArray()));
    }

    [Test]
    public async Task RequestMetadataShouldRoundTripAcrossSegmentsAndRejectImpossibleCount()
    {
        var metadata = new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "factory-a"),
            new KeyValuePair<string, string>("trace", "42"));
        var payload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteMetadata(payload, metadata);
        var decoded = ProtocolV2PayloadCodec.ReadMetadata(
            CreateSegmented(payload.WrittenMemory.ToArray(), 1));
        Ensure(decoded.Count == 2 && decoded[0].Value == "factory-a", "segmented metadata round-trip");

        var invalid = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteVarUInt32(invalid, uint.MaxValue);
        try
        {
            _ = ProtocolV2PayloadCodec.ReadMetadata(new ReadOnlySequence<byte>(invalid.WrittenMemory));
            throw new Exception("expected invalid metadata count");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
            await Task.CompletedTask;
        }
    }

    [Test]
    public async Task StreamAndWindowPayloadShapesShouldBeValidated()
    {
        var streamPayload = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(streamPayload, 65_535);
        var streamFrame = CreateFrame(
            ProtocolV2FrameType.StreamComplete,
            ProtocolV2FrameFlags.None,
            1,
            streamPayload);
        var streamSequence = new ReadOnlySequence<byte>(streamFrame);
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref streamSequence, Limits, out _, out var parsed), "UInt16 stream ID");
        Ensure(BinaryPrimitives.ReadUInt16LittleEndian(parsed.FirstSpan) == 65_535, "full UInt16 stream range");

        var windowPayload = new byte[sizeof(ushort) + sizeof(uint)];
        var invalidWindow = CreateFrame(
            ProtocolV2FrameType.WindowUpdate,
            ProtocolV2FrameFlags.None,
            1,
            windowPayload);
        await ExpectProtocolViolation(invalidWindow);

        var validWindowPayload = new PooledByteBufferWriter();
        var expectedUpdate = new ProtocolV2WindowUpdate(65_535, 1234);
        ProtocolV2PayloadCodec.WriteWindowUpdate(validWindowPayload, expectedUpdate);
        var updateFrame = CreateFrame(
            ProtocolV2FrameType.WindowUpdate,
            ProtocolV2FrameFlags.None,
            9,
            validWindowPayload.WrittenMemory.ToArray());
        var updateSequence = new ReadOnlySequence<byte>(updateFrame);
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref updateSequence, Limits, out _, out var updatePayload),
            "valid WindowUpdate should parse");
        Ensure(ProtocolV2PayloadCodec.ReadWindowUpdate(updatePayload) == expectedUpdate,
            "WindowUpdate should round-trip");
    }

    [Test]
    public void MultipleFramesFollowedByHalfFrameShouldPreserveRemainder()
    {
        var first = CreateFrame(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 1, []);
        var second = CreateFrame(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 2, []);
        var third = CreateFrame(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 3, []);
        var combined = new byte[first.Length + second.Length + 7];
        first.CopyTo(combined, 0);
        second.CopyTo(combined, first.Length);
        third.AsSpan(0, 7).CopyTo(combined.AsSpan(first.Length + second.Length));
        var sequence = new ReadOnlySequence<byte>(combined);

        Ensure(ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out var one, out _), "first frame");
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out var two, out _), "second frame");
        Ensure(!ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out _, out _), "half frame");
        Ensure(one.RequestId == 1 && two.RequestId == 2 && sequence.Length == 7, "remainder preserved");
    }

    private static byte[] MutateHeader(int? length = null, byte? type = null, byte? flags = null)
    {
        var frame = CreateFrame(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 1, []);
        if (length is { } payloadLength)
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1, 4), payloadLength);
        if (type is { } frameType)
            frame[5] = frameType;
        if (flags is { } frameFlags)
            frame[6] = frameFlags;
        return frame;
    }

    private static byte[] CreateFrame(
        ProtocolV2FrameType type,
        ProtocolV2FrameFlags flags,
        ulong requestId,
        byte[] payload)
    {
        var writer = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(writer, type, flags, requestId);
        writer.Write(payload);
        ProtocolV2FrameWriter.EndFrame(writer, token);
        return writer.WrittenMemory.ToArray();
    }

    private static async Task ExpectProtocolViolation(byte[] frame)
    {
        try
        {
            var sequence = new ReadOnlySequence<byte>(frame);
            _ = ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out _, out _);
            throw new Exception("expected ProtocolViolation");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
            await Task.CompletedTask;
        }
    }

    private static ReadOnlySequence<byte> CreateSegmented(byte[] bytes, int segmentBytes)
    {
        BufferSegment? first = null;
        BufferSegment? last = null;
        for (var offset = 0; offset < bytes.Length; offset += segmentBytes)
        {
            var length = Math.Min(segmentBytes, bytes.Length - offset);
            var current = new BufferSegment(bytes.AsMemory(offset, length));
            if (first is null)
                first = current;
            else
                last!.SetNext(current);
            last = current;
        }
        return first is null
            ? ReadOnlySequence<byte>.Empty
            : new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public BufferSegment SetNext(BufferSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
            return next;
        }
    }
}
