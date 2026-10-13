using System.Buffers;
using System.Buffers.Binary;

namespace SharpLink.UnitTests.Protocol;

public sealed class ProtocolV2RequestIdentityTests
{
    private static readonly RpcHash128 Hash = new(0x0102030405060708, 0x1112131415161718);

    [Test]
    public void OrdinaryRequestShouldHaveFixedIdentityBeforeBusinessBytes()
    {
        var identity = new ProtocolV2RequestIdentity(-13, 27, Hash, RpcMethodKind.Unary, 0);
        using var writer = new PooledByteBufferWriter();
        ProtocolV2RequestIdentityCodec.Write(writer, identity);
        Ensure(writer.WrittenCount == 36, "the common route has one bounded 36-byte identity prefix");
        writer.Write(new byte[] { 0xA5, 0x5A });
        var payload = new ReadOnlySequence<byte>(writer.WrittenMemory);
        var reader = new SequenceReader<byte>(payload);
        Ensure(ProtocolV2RequestIdentityCodec.Read(ref reader) == identity, "ordinary wire identity round-trips");
        Ensure(reader.Remaining == 2 && reader.UnreadSpan[0] == 0xA5,
            "identity parsing must leave business bytes untouched");
    }

    [Test]
    public void ReceiptRequirementsShouldRoundTripAcrossEveryFragmentBoundary()
    {
        var receipt = new ContractRegistrationReceipt(Guid.NewGuid(), Guid.NewGuid(), 17,
            -13, Hash, Guid.NewGuid());
        foreach (var requirement in Enum.GetValues<RpcRegistrationRequirement>())
        {
            var identity = new ProtocolV2RequestIdentity(-13, 27, Hash,
                RpcMethodKind.DuplexStreaming, 2, receipt, requirement);
            using var writer = new PooledByteBufferWriter();
            ProtocolV2RequestIdentityCodec.Write(writer, identity);
            Ensure(writer.WrittenCount == 92, "receipt extension is bounded and carries only scalar identity");
            var bytes = writer.WrittenMemory.ToArray();
            for (var split = 1; split < bytes.Length; split++)
            {
                var first = new Segment(bytes.AsMemory(0, split));
                var last = first.Append(bytes.AsMemory(split));
                var payload = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
                Ensure(ProtocolV2RequestIdentityCodec.GetPrefixLength(payload) == bytes.Length,
                    "fragmented receipt prefix length");
                var reader = new SequenceReader<byte>(payload);
                Ensure(ProtocolV2RequestIdentityCodec.Read(ref reader) == identity && reader.End,
                    "every fragmented receipt retains server, epoch, generation and exact registration");
            }
        }
    }

    [Test]
    public void MissingTruncatedOrUnknownIdentityMustFailClosed()
    {
        var receipt = new ContractRegistrationReceipt(Guid.NewGuid(), Guid.NewGuid(), 17,
            -13, Hash, Guid.NewGuid());
        using var writer = new PooledByteBufferWriter();
        ProtocolV2RequestIdentityCodec.Write(writer, new ProtocolV2RequestIdentity(-13, 27, Hash,
            RpcMethodKind.Unary, 0, receipt, RpcRegistrationRequirement.ExactRegistration));
        var bytes = writer.WrittenMemory.ToArray();
        for (var length = 0; length < bytes.Length; length++)
            ExpectViolation(bytes.AsMemory(0, length));
        bytes[35] = 4;
        ExpectViolation(bytes);
        bytes[35] = 0;
        bytes.AsSpan(16, 16).Clear();
        ExpectViolation(bytes.AsMemory(0, 36));
    }

    [Test]
    public void UntrustedShapeCannotStartAnUnaccountedClientStream()
    {
        foreach (var identity in new[]
        {
            new ProtocolV2RequestIdentity(1, 2, Hash, RpcMethodKind.Unary, 1),
            new ProtocolV2RequestIdentity(1, 2, Hash, RpcMethodKind.ClientStreaming, 0),
            new ProtocolV2RequestIdentity(1, 2, Hash, (RpcMethodKind)255, 0),
            new ProtocolV2RequestIdentity(1, 2, Hash, RpcMethodKind.Unary, 0,
                Requirement: RpcRegistrationRequirement.MinimumVersion)
        })
        {
            using var writer = new PooledByteBufferWriter();
            try
            {
                ProtocolV2RequestIdentityCodec.Write(writer, identity);
                throw new InvalidOperationException("Invalid request identity was accepted.");
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
            {
                Ensure(writer.WrittenCount == 0, "invalid identity must not publish a partial request");
            }
        }
    }

    [Test]
    public void HandshakeMustCarryTheSameServerAndRegistryTimeline()
    {
        var expected = new ProtocolV2HandshakeResponse(ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.ContractManifest, 4096, 1024, 2048,
            ServerInstanceId: Guid.NewGuid(), RegistryEpoch: Guid.NewGuid());
        using var writer = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteHandshakeResponse(writer, expected);
        var actual = ProtocolV2PayloadCodec.ReadHandshakeResponse(
            new ReadOnlySequence<byte>(writer.WrittenMemory), new SharpLinkProtocolOptions());
        Ensure(actual == expected, "server identity comes from the connection handshake, independent of manifest broadcasts");
        Ensure(ProtocolV2Constants.MinimumCompatibleMinorVersion == 5,
            "the previous request format must not be negotiated");
    }

    [Test]
    public void StreamAdmissionFrameMustBeEmptyUnflaggedAndRequestScoped()
    {
        var bytes = new byte[ProtocolV2Constants.HeaderBytes];
        bytes[0] = ProtocolV2Constants.Magic;
        bytes[5] = (byte)ProtocolV2FrameType.RequestAccepted;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(7), 19);
        var limits = new SharpLinkProtocolOptions();
        for (var split = 1; split < bytes.Length; split++)
        {
            var first = new Segment(bytes.AsMemory(0, split));
            var last = first.Append(bytes.AsMemory(split));
            var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
            Ensure(ProtocolV2FrameParser.TryReadFrame(ref sequence, limits, out var header, out var payload) &&
                   header.Type == ProtocolV2FrameType.RequestAccepted && header.RequestId == 19 &&
                   payload.IsEmpty && sequence.IsEmpty, "fragmented admission ACK retains its pending request identity");
        }

        var withPayload = new byte[bytes.Length + 1];
        bytes.CopyTo(withPayload, 0);
        BinaryPrimitives.WriteInt32LittleEndian(withPayload.AsSpan(1), 1);
        ExpectInvalidFrame(withPayload);
        bytes[6] = (byte)ProtocolV2FrameFlags.OneWay;
        ExpectInvalidFrame(bytes);
        bytes[6] = 0;
        bytes.AsSpan(7).Clear();
        ExpectInvalidFrame(bytes);
    }

    private static void ExpectInvalidFrame(byte[] bytes)
    {
        try
        {
            var sequence = new ReadOnlySequence<byte>(bytes);
            _ = ProtocolV2FrameParser.TryReadFrame(ref sequence, new SharpLinkProtocolOptions(), out _, out _);
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
            return;
        }
        throw new InvalidOperationException("Malformed stream admission frame was accepted.");
    }

    private static void ExpectViolation(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));
            _ = ProtocolV2RequestIdentityCodec.Read(ref reader);
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
            return;
        }
        throw new InvalidOperationException("Malformed request identity was accepted.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
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
}
