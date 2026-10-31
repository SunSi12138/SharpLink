using System.Buffers.Binary;
using System.IO.Pipelines;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class Protocol3RequestEnvelopeOwnershipTests
{
    [Test]
    public async Task BothRequestTypesPreserveCapturedIdentityMetadataAndDeadlineAfterDecode()
    {
        await using var session = Session();
        foreach (var type in new[] { ProtocolV2FrameType.Request, ProtocolV2FrameType.OneWayStreamRequest })
        {
            var flags = ProtocolV2FrameFlags.HasTimeBudget | ProtocolV2FrameFlags.HasMetadata | ProtocolV2FrameFlags.Compressed;
            if (type == ProtocolV2FrameType.OneWayStreamRequest) flags |= ProtocolV2FrameFlags.OneWay;
            var bytes = Payload(type, flags, 2);
            var encoded = new ReadOnlySequence<byte>(bytes);
            var original = ServerRequestEnvelopeReader.Read(session, encoded, flags, 1024, TimeProvider.System, frameType: type);
            Ensure(original.FixedPrefixBytes == (type == ProtocolV2FrameType.Request ? 16 : 18));
            Ensure(original.ClientStreamCount == (type == ProtocolV2FrameType.Request ? 0 : 2));
            var decodedBytes = new byte[original.PrefixBytes + 3];
            bytes.AsSpan(0, original.PrefixBytes).CopyTo(decodedBytes);
            decodedBytes[^3] = 7; decodedBytes[^2] = 8; decodedBytes[^1] = 9;
            var rebound = ServerRequestEnvelopeReader.ReadDecoded(session, new(decodedBytes), encoded,
                in original, flags, 1024, TimeProvider.System);
            Ensure(rebound.BindingId == 0x10000 && rebound.MethodHash == -42 && rebound.FrameType == type);
            Ensure(rebound.RpcDeadline.Timestamp == original.RpcDeadline.Timestamp);
            Ensure(ReferenceEquals(rebound.Metadata, original.Metadata));
            Ensure(rebound.Arguments.ToArray().AsSpan().SequenceEqual(new byte[] { 7, 8, 9 }));
            foreach (var offset in new[] { 0, 8, original.FixedPrefixBytes, original.PrefixBytes - 1 })
            {
                var changed = (byte[])decodedBytes.Clone(); changed[offset] ^= 1;
                ExpectViolation(() => ServerRequestEnvelopeReader.ReadDecoded(session, new(changed), encoded,
                    in original, flags, 1024, TimeProvider.System));
            }
        }
    }

    [Test]
    public async Task StreamCountIsExplicitBoundedAndNeverGuessedFromFlagsOrRegistry()
    {
        await using var session = Session();
        var flags = ProtocolV2FrameFlags.OneWay;
        foreach (var count in new ushort[] { 0, 3, 128, ushort.MaxValue })
        {
            var payload = Payload(ProtocolV2FrameType.OneWayStreamRequest, flags, count);
            ExpectViolation(() => ServerRequestEnvelopeReader.Read(session, new(payload), flags, 1024,
                TimeProvider.System, frameType: ProtocolV2FrameType.OneWayStreamRequest));
        }
        var bytes = Payload(ProtocolV2FrameType.OneWayStreamRequest, flags, 2);
        ExpectViolation(() => ServerRequestEnvelopeReader.Read(session, new(bytes.AsMemory(0, 17)), flags,
            1024, TimeProvider.System, frameType: ProtocolV2FrameType.OneWayStreamRequest));
        var ordinary = ServerRequestEnvelopeReader.Read(session, new(Payload(ProtocolV2FrameType.Request, flags, 0)),
            flags, 1024, TimeProvider.System);
        Ensure(ordinary.ClientStreamCount == 0 && ordinary.PrefixBytes == 16);
    }

    private static byte[] Payload(ProtocolV2FrameType type, ProtocolV2FrameFlags flags, ushort count)
    {
        var writer = new ArrayBufferWriter<byte>();
        var length = type == ProtocolV2FrameType.Request ? 16 : 18;
        var span = writer.GetSpan(length);
        BinaryPrimitives.WriteUInt64LittleEndian(span, 0x10000);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], -42);
        if (length == 18) BinaryPrimitives.WriteUInt16LittleEndian(span[16..], count);
        writer.Advance(length);
        if ((flags & ProtocolV2FrameFlags.HasTimeBudget) != 0)
        {
            BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(8), TimeSpan.FromSeconds(10).Ticks);
            writer.Advance(8);
        }
        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0)
        {
            var metadata = new SharpLinkMetadata(new KeyValuePair<string, string>("tenant", "one"));
            ProtocolV2PayloadCodec.WriteVarUInt32(writer, (uint)ProtocolV2PayloadCodec.GetMetadataPayloadLength(metadata));
            ProtocolV2PayloadCodec.WriteMetadata(writer, metadata);
        }
        writer.Write(new byte[] { 1, 0, 0, 0, 0x7f });
        return writer.WrittenSpan.ToArray();
    }

    private static RpcSession Session()
    {
        var input = new Pipe(); var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport("protocol3-envelope", input.Reader,
            output.Writer, RpcSessionTestFixture.ServerOptions(), completeHandshake: false);
        var context = session.RuntimeContext;
        var authority = new Protocol3BindingAuthority(Guid.NewGuid(), Guid.NewGuid(),
            Protocol3BindingLimits.Default with { MaxClientStreamsPerCall = 2 });
        Ensure(session.TryCompleteHandshake(new NegotiatedSessionOptions(0,
            ProtocolV2Negotiator.MandatoryCapabilities | ProtocolV2Capabilities.Metadata,
            context.Protocol.MaxFramePayloadBytes, context.FlowControl.StreamReceiveWindowBytes,
            context.FlowControl.ConnectionReceiveWindowBytes, authority: authority)));
        return session;
    }

    private static void ExpectViolation(Action action)
    {
        try { action(); }
        catch (SharpLinkProtocolViolationException) { return; }
        throw new InvalidOperationException("Expected an invalid Protocol3 request envelope.");
    }

    private static void Ensure(bool value)
    {
        if (!value) throw new InvalidOperationException("Protocol3 envelope ownership assertion failed.");
    }
}
