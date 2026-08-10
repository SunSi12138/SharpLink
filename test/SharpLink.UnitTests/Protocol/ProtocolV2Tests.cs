using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
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
    public async Task RawFrameWriterAndTokenShouldRemainInternalImplementationDetails()
    {
        await Assert.That(typeof(ProtocolV2FrameWriter).IsPublic).IsFalse();
        await Assert.That(typeof(ProtocolV2FrameToken).IsPublic).IsFalse();
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
            new byte[] { 1, 2, 3, 4 },
            new[] { "brotli", "zstd-dict/0123abcd" });
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
        Ensure(decodedRequest.CompressionProfiles.Span.SequenceEqual(request.CompressionProfiles.Span),
            "handshake compression profiles");

        var responsePayload = new PooledByteBufferWriter();
        var response = new ProtocolV2HandshakeResponse(
            0,
            ProtocolV2Capabilities.FlowControl | ProtocolV2Capabilities.Compression,
            1024 * 1024,
            512 * 1024,
            8 * 1024 * 1024,
            "brotli");
        ProtocolV2PayloadCodec.WriteHandshakeResponse(responsePayload, response);
        var decodedResponse = ProtocolV2PayloadCodec.ReadHandshakeResponse(
            new ReadOnlySequence<byte>(responsePayload.WrittenMemory), Limits);
        Ensure(decodedResponse == response, "handshake response round-trip");
    }

    [Test]
    public void HandshakeResponseShouldRequireCompressionCapabilityAndProfileTogether()
    {
        var withoutCapability = new ProtocolV2HandshakeResponse(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.FlowControl,
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            "brotli");
        var withoutProfile = withoutCapability with
        {
            NegotiatedCapabilities = ProtocolV2Capabilities.FlowControl | ProtocolV2Capabilities.Compression,
            CompressionProfile = null
        };

        var writeProfileWithoutCapability = CaptureException(() =>
            ProtocolV2PayloadCodec.WriteHandshakeResponse(new PooledByteBufferWriter(), withoutCapability));
        var writeCapabilityWithoutProfile = CaptureException(() =>
            ProtocolV2PayloadCodec.WriteHandshakeResponse(new PooledByteBufferWriter(), withoutProfile));

        var coherentPayload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteHandshakeResponse(
            coherentPayload,
            withoutCapability with
            {
                NegotiatedCapabilities = ProtocolV2Capabilities.FlowControl | ProtocolV2Capabilities.Compression
            });
        var profileWithoutCapabilityBytes = coherentPayload.WrittenMemory.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            profileWithoutCapabilityBytes.AsSpan(sizeof(ushort), sizeof(ulong)),
            (ulong)ProtocolV2Capabilities.FlowControl);
        var readProfileWithoutCapability = CaptureException(() =>
            ProtocolV2PayloadCodec.ReadHandshakeResponse(
                new ReadOnlySequence<byte>(profileWithoutCapabilityBytes), Limits));

        var capabilityWithoutProfilePayload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteHandshakeResponse(
            capabilityWithoutProfilePayload,
            withoutCapability with
            {
                NegotiatedCapabilities = ProtocolV2Capabilities.FlowControl,
                CompressionProfile = null
            });
        var capabilityWithoutProfileBytes = capabilityWithoutProfilePayload.WrittenMemory.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            capabilityWithoutProfileBytes.AsSpan(sizeof(ushort), sizeof(ulong)),
            (ulong)(ProtocolV2Capabilities.FlowControl | ProtocolV2Capabilities.Compression));
        var readCapabilityWithoutProfile = CaptureException(() =>
            ProtocolV2PayloadCodec.ReadHandshakeResponse(
                new ReadOnlySequence<byte>(capabilityWithoutProfileBytes), Limits));

        Ensure(writeProfileWithoutCapability is ArgumentException,
            "the writer must reject a compression profile without the negotiated capability");
        Ensure(writeCapabilityWithoutProfile is ArgumentException,
            "the writer must reject negotiated compression without a selected profile");
        Ensure(readProfileWithoutCapability is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation },
            "the reader must reject a compression profile without the negotiated capability");
        Ensure(readCapabilityWithoutProfile is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation },
            "the reader must reject negotiated compression without a selected profile");
    }

    [Test]
    public void HandshakeCapabilitiesShouldRejectInconsistentOrUnknownSets()
    {
        var inconsistent = new ProtocolV2HandshakeRequest(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.Metadata,
            ProtocolV2Capabilities.FlowControl,
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            ReadOnlyMemory<byte>.Empty);
        var writeFailure = CaptureException(() =>
            ProtocolV2PayloadCodec.WriteHandshakeRequest(
                new PooledByteBufferWriter(), inconsistent, Limits));

        var requestPayload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteHandshakeRequest(
            requestPayload,
            inconsistent with { RequiredCapabilities = ProtocolV2Capabilities.None },
            Limits);
        var requestBytes = requestPayload.WrittenMemory.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            requestBytes.AsSpan(sizeof(ushort) + sizeof(ulong), sizeof(ulong)),
            (ulong)ProtocolV2Capabilities.FlowControl);
        var readRequestFailure = CaptureException(() =>
            ProtocolV2PayloadCodec.ReadHandshakeRequest(
                new ReadOnlySequence<byte>(requestBytes), Limits));

        var responsePayload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteHandshakeResponse(
            responsePayload,
            new ProtocolV2HandshakeResponse(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                4 * 1024 * 1024,
                1024 * 1024,
                16 * 1024 * 1024));
        var responseBytes = responsePayload.WrittenMemory.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            responseBytes.AsSpan(sizeof(ushort), sizeof(ulong)),
            1UL << 63);
        var readResponseFailure = CaptureException(() =>
            ProtocolV2PayloadCodec.ReadHandshakeResponse(
                new ReadOnlySequence<byte>(responseBytes), Limits));

        Ensure(writeFailure is ArgumentException,
            "outbound required capabilities must be a subset of supported capabilities");
        Ensure(readRequestFailure is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation },
            "inbound inconsistent request capabilities must be a protocol violation");
        Ensure(readResponseFailure is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation },
            "an unknown negotiated response capability must be a protocol violation");
    }

    [Test]
    public async Task ControlWritersShouldClassifyInvalidLocalEnumsAsArgumentsWithoutPartialOutput()
    {
        using var writer = new PooledByteBufferWriter();
        var cancel = CaptureException(() => ProtocolV2PayloadCodec.WriteCancelReason(
            writer, (ProtocolV2CancelReason)byte.MaxValue));
        var health = CaptureException(() => ProtocolV2PayloadCodec.WriteHealthResponse(
            writer, (SharpLinkHealthStatus)byte.MaxValue));
        await Assert.That(cancel).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(health).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(writer.WrittenCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandshakeWritersShouldClassifyInvalidLocalLimitsAsArgumentsWithoutPartialOutput()
    {
        using var writer = new PooledByteBufferWriter();
        var requestFailure = CaptureException(() => ProtocolV2PayloadCodec.WriteHandshakeRequest(
            writer,
            new ProtocolV2HandshakeRequest(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                ProtocolV2Capabilities.None,
                MaxFramePayloadBytes: 1,
                StreamReceiveWindowBytes: 1,
                ConnectionReceiveWindowBytes: 1,
                ReadOnlyMemory<byte>.Empty),
            Limits));
        var responseFailure = CaptureException(() => ProtocolV2PayloadCodec.WriteHandshakeResponse(
            writer,
            new ProtocolV2HandshakeResponse(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                MaxFramePayloadBytes: 1,
                StreamReceiveWindowBytes: 1,
                ConnectionReceiveWindowBytes: 1)));

        await Assert.That(requestFailure).IsAssignableTo<ArgumentException>();
        await Assert.That(responseFailure).IsAssignableTo<ArgumentException>();
        await Assert.That(writer.WrittenCount).IsEqualTo(0);
    }

    [Test]
    public async Task CancelReasonShouldRoundTripAndEnforceNegotiatedShape()
    {
        foreach (var reason in new[]
                 {
                     ProtocolV2CancelReason.UserCancellation,
                     ProtocolV2CancelReason.DeadlineExceeded,
                     ProtocolV2CancelReason.ConsumerAbandoned
                 })
        {
            var roundTripWriter = new PooledByteBufferWriter();
            ProtocolV2PayloadCodec.WriteCancelReason(roundTripWriter, reason);
            Ensure(
                ProtocolV2PayloadCodec.ReadCancelReason(
                    new ReadOnlySequence<byte>(roundTripWriter.WrittenMemory)) == reason,
                $"cancel reason {reason} round-trip");
        }

        var payloadWriter = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteCancelReason(payloadWriter, ProtocolV2CancelReason.ConsumerAbandoned);
        var payload = new ReadOnlySequence<byte>(payloadWriter.WrittenMemory);

        var frame = CreateFrame(
            ProtocolV2FrameType.Cancel,
            ProtocolV2FrameFlags.None,
            1,
            payloadWriter.WrittenMemory.ToArray());
        var sequence = new ReadOnlySequence<byte>(frame);
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out _, out _),
            "static parser should accept a bounded one-byte Cancel payload");

        var input = new Pipe();
        var output = new Pipe();
        await using var session = new RpcSession(
            "cancel-shape",
            input.Reader,
            output.Writer,
            static () => { },
            static () => true,
            RpcSessionTestFixture.ClientOptions());

        session.NegotiatedCapabilities = ProtocolV2Capabilities.CancellationReason;
        Ensure(
            session.ReadNegotiatedCancelReason(payload) == ProtocolV2CancelReason.ConsumerAbandoned,
            "negotiated reason should decode");
        await ExpectProtocolViolation(() =>
            session.ReadNegotiatedCancelReason(ReadOnlySequence<byte>.Empty));

        session.NegotiatedCapabilities = ProtocolV2Capabilities.None;
        Ensure(
            session.ReadNegotiatedCancelReason(ReadOnlySequence<byte>.Empty) ==
            ProtocolV2CancelReason.Unspecified,
            "legacy empty Cancel should decode as unspecified");
        await ExpectProtocolViolation(() => session.ReadNegotiatedCancelReason(payload));
        await ExpectProtocolViolation(() => ProtocolV2PayloadCodec.ReadCancelReason(
            new ReadOnlySequence<byte>(new byte[] { byte.MaxValue })));
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
    public void BinaryErrorShouldPreserveResourceReasonAtOneByteMessageLimit()
    {
        var wireException = SharpLinkResourceExhaustion.CreateWire(
            SharpLinkResourceExhaustion.ServerCallCapacity,
            "Server call capacity is exhausted (server_call_capacity).");
        using var payload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteError(
            payload,
            wireException.Code,
            wireException.Message,
            maxMessageBytes: 1,
            out var truncated);

        var error = ProtocolV2PayloadCodec.ReadError(
            new ReadOnlySequence<byte>(payload.WrittenMemory),
            ProtocolV2FrameFlags.Error | ProtocolV2FrameFlags.Truncated,
            maxMessageBytes: 1);
        var restored = SharpLinkResourceExhaustion.CreateRemote(error.Code, error.Message);

        Ensure(truncated, "the human-readable suffix should be truncated");
        Ensure(
            SharpLinkResourceExhaustion.GetReason(restored) ==
            SharpLinkResourceExhaustion.ServerCallCapacity,
            "the one-byte wire discriminator must restore the stable reason");
    }

    [Test]
    public void BinaryErrorWriterShouldRejectUndefinedErrorCodes()
    {
        using var payload = new PooledByteBufferWriter();
        try
        {
            ProtocolV2PayloadCodec.WriteError(
                payload,
                (SharpLinkErrorCode)22,
                "undefined",
                Limits.MaxErrorMessageBytes,
                out _);
            throw new Exception("expected undefined error-code rejection");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        Ensure(payload.WrittenCount == 0, "undefined error codes must not write a partial payload");
    }

    [Test]
    public async Task BinaryErrorShouldRejectReservedUnknownCodeInBothDirections()
    {
        using var payload = new PooledByteBufferWriter();
        var writeFailure = CaptureException(() => ProtocolV2PayloadCodec.WriteError(
            payload,
            SharpLinkErrorCode.Unknown,
            "reserved",
            Limits.MaxErrorMessageBytes,
            out _));
        var readFailure = CaptureException(() => ProtocolV2PayloadCodec.ReadError(
            new ReadOnlySequence<byte>(new byte[] { 0, 0, 0 }),
            ProtocolV2FrameFlags.Error,
            Limits.MaxErrorMessageBytes));

        await Assert.That(writeFailure).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(payload.WrittenCount).IsEqualTo(0);
        await Assert.That(readFailure).IsAssignableTo<SharpLinkException>();
        await Assert.That((readFailure as SharpLinkException)?.Code)
            .IsEqualTo(SharpLinkErrorCode.ProtocolViolation);
    }

    [Test]
    public async Task BinaryErrorShouldRejectInvalidUtf8()
    {
        var payload = new byte[]
        {
            (byte)SharpLinkErrorCode.Unavailable,
            0,
            2,
            0xC3,
            0x28
        };

        await ExpectProtocolViolation(() => ProtocolV2PayloadCodec.ReadError(
            CreateSegmented(payload, 1),
            ProtocolV2FrameFlags.Error,
            Limits.MaxErrorMessageBytes));
        await ExpectProtocolViolation(CreateFrame(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.Error,
            1,
            payload));
    }

    [Test]
    public async Task GeneratedDtoStringShouldRejectInvalidUtf8()
    {
        var payload = new byte[]
        {
            2, 0, 0, 0,
            0xC3, 0x28
        };
        var contiguousFailure = CaptureException(() =>
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(payload));
            _ = RpcGeneratedCodecWire.ReadString(ref reader);
        });
        var segmentedFailure = CaptureException(() =>
        {
            var reader = new SequenceReader<byte>(CreateSegmented(payload, 1));
            _ = RpcGeneratedCodecWire.ReadString(ref reader);
        });

        Ensure(contiguousFailure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "contiguous generated string must reject invalid UTF-8");
        Ensure(segmentedFailure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "segmented generated string must reject invalid UTF-8");

        var validReplacementPayload = new byte[] { 3, 0, 0, 0, 0xEF, 0xBF, 0xBD };
        var validReader = new SequenceReader<byte>(CreateSegmented(validReplacementPayload, 1));
        Ensure(RpcGeneratedCodecWire.ReadString(ref validReader) == "\uFFFD",
            "a canonically encoded replacement character must remain valid");
        await Task.CompletedTask;
    }

    [Test]
    public async Task LengthVarintsShouldRejectOverlongEncodings()
    {
        await ExpectProtocolViolation(() => ProtocolV2PayloadCodec.ReadMetadata(
            new ReadOnlySequence<byte>(new byte[] { 0x80, 0x00 })));

        var requestPayload = new byte[ProtocolV2Constants.RequestPrefixBytes + 2];
        requestPayload[^2] = 0x80;
        requestPayload[^1] = 0x00;
        await ExpectProtocolViolation(CreateFrame(
            ProtocolV2FrameType.Request,
            ProtocolV2FrameFlags.HasMetadata,
            1,
            requestPayload));
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
    public async Task HealthControlFramesShouldRequireBoundedPayloadAndRequestId()
    {
        var request = CreateFrame(
            ProtocolV2FrameType.HealthCheck,
            ProtocolV2FrameFlags.None,
            7,
            []);
        var requestSequence = new ReadOnlySequence<byte>(request);
        Ensure(ProtocolV2FrameParser.TryReadFrame(
            ref requestSequence, Limits, out var requestHeader, out var requestPayload),
            "health request should parse");
        Ensure(requestHeader.RequestId == 7 && requestPayload.IsEmpty, "health request shape");

        var response = CreateFrame(
            ProtocolV2FrameType.HealthResponse,
            ProtocolV2FrameFlags.None,
            7,
            [(byte)SharpLinkHealthStatus.Ready]);
        var responseSequence = new ReadOnlySequence<byte>(response);
        Ensure(ProtocolV2FrameParser.TryReadFrame(
            ref responseSequence, Limits, out _, out var responsePayload),
            "health response should parse");
        Ensure(responsePayload.FirstSpan[0] == (byte)SharpLinkHealthStatus.Ready,
            "health response status");

        await ExpectProtocolViolation(CreateFrame(
            ProtocolV2FrameType.HealthCheck,
            ProtocolV2FrameFlags.None,
            0,
            []));
        await ExpectProtocolViolation(CreateFrame(
            ProtocolV2FrameType.HealthCheck,
            ProtocolV2FrameFlags.None,
            7,
            [1]));
        await ExpectProtocolViolation(CreateFrame(
            ProtocolV2FrameType.HealthResponse,
            ProtocolV2FrameFlags.None,
            7,
            [(byte)SharpLinkHealthStatus.Ready, 0]));
        await ExpectProtocolViolation(CreateFrame(
            ProtocolV2FrameType.HealthResponse,
            ProtocolV2FrameFlags.None,
            7,
            [byte.MaxValue]));
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

    private static async Task ExpectProtocolViolation(Action action)
    {
        try
        {
            action();
            throw new Exception("expected ProtocolViolation");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
            await Task.CompletedTask;
        }
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
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
