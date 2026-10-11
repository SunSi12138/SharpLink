using System.Buffers.Binary;
using System.IO.Pipelines;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class ContractIdentityEnvelopeTests
{
    private static readonly RpcHash128 Identity = new(0xF102030405060708, 0xF112131415161718);

    [Test]
    public async Task IdentityShouldRoundTripAcrossEverySequenceBoundary()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.ContractAdmission);
        var bytes = Payload([9, 8, 7]);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var first = new Segment(bytes.AsMemory(0, split));
            var last = first.Append(bytes.AsMemory(split));
            var request = ServerRequestEnvelopeReader.Read(session, new(first, 0, last, last.Memory.Length),
                ProtocolV2FrameFlags.HasContractIdentity, 1024, TimeProvider.System);
            Ensure(request.ContractHash == Identity, "exact identity survives segmentation");
            Ensure(request.InterfaceHash == 12 && request.MethodHash == 34, "routing fields unchanged");
            Ensure(request.Arguments.ToArray().AsSpan().SequenceEqual(new byte[] { 9, 8, 7 }), "identity excluded from arguments");
        }
    }

    [Test]
    public async Task IdentityShouldRejectTruncationEmptyAndUnnegotiatedFields()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.ContractAdmission);
        var bytes = Payload([]);
        for (var length = 16; length < 34; length++)
        {
            var truncated = bytes[..length];
            Reject(() => Read(session, truncated));
        }
        bytes.AsSpan(16, 16).Clear();
        Reject(() => Read(session, bytes));
        await using var legacy = CreateSession(ProtocolV2Capabilities.None);
        Reject(() => Read(legacy, Payload([])));
    }

    [Test]
    public async Task DecodedMetadataReuseShouldRetainIdentityAndConsumeItBeforeMetadata()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.ContractAdmission | ProtocolV2Capabilities.Metadata);
        const ProtocolV2FrameFlags flags = ProtocolV2FrameFlags.HasContractIdentity |
            ProtocolV2FrameFlags.HasMetadata | ProtocolV2FrameFlags.Compressed;
        var encoded = new ReadOnlySequence<byte>(Payload([1, 0, 1, 2]));
        var decoded = new ReadOnlySequence<byte>(Payload([1, 0, 3, 4, 5]));
        var original = ServerRequestEnvelopeReader.Read(session, encoded, flags, 1024, TimeProvider.System);
        var rebound = ServerRequestEnvelopeReader.ReadDecoded(session, decoded, encoded, original, flags, 1024, TimeProvider.System);
        Ensure(rebound.ContractHash == Identity, "reuse preserves admitted identity");
        Ensure(ReferenceEquals(original.Metadata, rebound.Metadata), "unchanged metadata reused");
        Ensure(rebound.Arguments.ToArray().AsSpan().SequenceEqual(new byte[] { 3, 4, 5 }), "decoded argument offset");
    }

    [Test]
    public void CompressionLengthShouldKeepIdentityOutsideBusinessPayload()
    {
        var bytes = Payload(new byte[4]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), 1024 * 1024);
        const ProtocolV2FrameFlags flags = ProtocolV2FrameFlags.HasContractIdentity | ProtocolV2FrameFlags.Compressed;
        Ensure(RpcSession.ReadCompressedOriginalLength(ProtocolV2FrameType.Request, flags, new(bytes)) == 1024 * 1024,
            "compressed length follows identity");
        Ensure(RpcSession.ReadCompressedDecodedPayloadLength(ProtocolV2FrameType.Request, flags, new(bytes)) == 34 + 1024 * 1024,
            "persistent decoded allocation includes identity prefix");
    }

    [Test]
    public void IdentityFlagShouldBeRequestOnlyAliasOfErrorTruncation()
    {
        using var writer = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(writer, ProtocolV2FrameType.Request, ProtocolV2FrameFlags.HasContractIdentity, 1);
        writer.Write(Payload([]));
        ProtocolV2FrameWriter.EndFrame(writer, token);
        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref buffer, new(), out var header, out var payload), "request parsed");
        Ensure(header.Flags == ProtocolV2FrameFlags.HasContractIdentity && payload.Length == 34, "request alias accepted");
        writer.WrittenSpan[5] = (byte)ProtocolV2FrameType.Response;
        Reject(() => Parse(writer.WrittenMemory));
    }

    private static void Parse(ReadOnlyMemory<byte> bytes)
    {
        var buffer = new ReadOnlySequence<byte>(bytes);
        ProtocolV2FrameParser.TryReadFrame(ref buffer, new(), out _, out _);
    }

    private static void Read(RpcSession session, byte[] bytes)
        => ServerRequestEnvelopeReader.Read(session, new(bytes), ProtocolV2FrameFlags.HasContractIdentity, 1024, TimeProvider.System);

    private static byte[] Payload(byte[] tail)
    {
        var bytes = new byte[34 + tail.Length];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, 12);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), 34);
        ProtocolV2ContractAdmissionCodec.Write(bytes.AsSpan(16), new ContractRequirement(12, Identity));
        tail.CopyTo(bytes, 34);
        return bytes;
    }

    private static RpcSession CreateSession(ProtocolV2Capabilities capabilities)
    {
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport("contract-identity",
            new Pipe().Reader, new Pipe().Writer, RpcSessionTestFixture.ServerOptions(), completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, capabilities);
        return session;
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation) { return; }
        throw new Exception("Expected contract identity protocol violation.");
    }
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
