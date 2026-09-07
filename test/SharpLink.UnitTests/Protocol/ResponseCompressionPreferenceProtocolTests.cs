using System.Buffers;
using System.Buffers.Binary;

namespace SharpLink.UnitTests.Protocol;

public class ResponseCompressionPreferenceProtocolTests
{
    [Test]
    public void HandshakeShouldRoundTripFixedPreferenceGeneration()
    {
        var limits = new SharpLinkProtocolOptions();
        using var writer = new PooledByteBufferWriter();
        var request = new ProtocolV2HandshakeRequest(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.Compression,
            ProtocolV2Capabilities.None,
            SharpLinkProtocolOptions.MinMaxFramePayloadBytes,
            1024,
            4096,
            ReadOnlyMemory<byte>.Empty,
            new[] { "test-profile" },
            ResponseCompressionPreferenceGeneration: 17,
            AllowResponseCompression: false);

        ProtocolV2PayloadCodec.WriteHandshakeRequest(writer, request, limits);
        var decoded = ProtocolV2PayloadCodec.ReadHandshakeRequest(
            new ReadOnlySequence<byte>(writer.WrittenMemory),
            limits);

        Ensure(decoded.ResponseCompressionPreferenceGeneration == 17, "handshake preference generation");
        Ensure(!decoded.AllowResponseCompression, "handshake preference value");
        Ensure(decoded.CompressionProfiles.Span.SequenceEqual(request.CompressionProfiles.Span), "handshake profiles");
    }

    [Test]
    public void PreferenceControlFramesShouldBeFixedAndStrict()
    {
        var limits = new SharpLinkProtocolOptions();
        using var writer = new PooledByteBufferWriter();
        using (writer.BeginPacketScope(
                   ProtocolV2FrameType.ResponseCompressionPreferenceUpdate,
                   ProtocolV2FrameFlags.None,
                   0))
        {
            ProtocolV2PayloadCodec.WriteResponseCompressionPreferenceUpdate(
                writer,
                new ProtocolV2ResponseCompressionPreferenceUpdate(23, false));
        }

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref buffer, limits, out var header, out var payload), "update frame parse");
        Ensure(header.Type == ProtocolV2FrameType.ResponseCompressionPreferenceUpdate, "update frame type");
        Ensure(header.RequestId == 0, "update request id");
        var update = ProtocolV2PayloadCodec.ReadResponseCompressionPreferenceUpdate(payload);
        Ensure(update.Generation == 23 && !update.AllowResponseCompression, "update payload round trip");

        using var malformed = new PooledByteBufferWriter();
        using (malformed.BeginPacketScope(
                   ProtocolV2FrameType.ResponseCompressionPreferenceUpdate,
                   ProtocolV2FrameFlags.None,
                   0))
        {
            var generation = malformed.GetSpan(sizeof(ulong));
            BinaryPrimitives.WriteUInt64LittleEndian(generation, 24);
            malformed.Advance(sizeof(ulong));
            var preference = malformed.GetSpan(1);
            preference[0] = 2;
            malformed.Advance(1);
        }
        var malformedBuffer = new ReadOnlySequence<byte>(malformed.WrittenMemory);
        var failed = false;
        try
        {
            _ = ProtocolV2FrameParser.TryReadFrame(ref malformedBuffer, limits, out _, out _);
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
            failed = true;
        }
        Ensure(failed, "invalid preference byte must be rejected");
    }

    private static void Ensure(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {description}.");
    }
}
