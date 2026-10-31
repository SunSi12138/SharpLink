using System.Buffers.Binary;

namespace SharpLink.UnitTests.Protocol;

/// <summary>Source-only Protocol3 grammar regressions; historical Protocol2 fixtures remain unchanged.</summary>
public class Protocol3FrameAndHandshakeTests
{
    private static readonly SharpLinkProtocolOptions Limits = new();
    private const ProtocolV2Capabilities Mandatory = ProtocolV2Capabilities.FlowControl |
        ProtocolV2Capabilities.CancellationReason | ProtocolV2Capabilities.ContractManifest;

    [Test]
    public void OnlyNewMagicAndPositive63CorrelationAreAccepted()
    {
        var valid = Frame(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, long.MaxValue, new byte[] { 0 });
        Parse(valid);
        var old = (byte[])valid.Clone();
        old[0] = 0x89;
        ExpectViolation(() => Parse(old));
        foreach (var id in new[] { 0UL, (ulong)long.MaxValue + 1, ulong.MaxValue })
            ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, id, new byte[] { 0 })));
        for (var type = 24; type <= byte.MaxValue; type++)
            ExpectViolation(() => Parse(Frame((ProtocolV2FrameType)type, ProtocolV2FrameFlags.None, 1, Array.Empty<byte>())));
    }

    [Test]
    public void CancellationHasExactlyOneKnownReasonByte()
    {
        for (byte reason = 0; reason <= 3; reason++)
            Parse(Frame(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 1, new[] { reason }));
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 4 }, new byte[] { 0, 0 } })
            ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 1, bytes)));
    }

    [Test]
    public void ExplicitOneWayStreamPrefixAndAcceptanceShapeAreMandatory()
    {
        var prefix = new byte[18];
        prefix[16] = 1;
        Parse(Frame(ProtocolV2FrameType.OneWayStreamRequest, ProtocolV2FrameFlags.OneWay, 1, prefix));
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.OneWayStreamRequest, ProtocolV2FrameFlags.None, 1, prefix)));
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.OneWayStreamRequest,
            ProtocolV2FrameFlags.OneWay | ProtocolV2FrameFlags.HasReturn, 1, prefix)));
        foreach (var count in new ushort[] { 0, 128, ushort.MaxValue })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(16), count);
            ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.OneWayStreamRequest, ProtocolV2FrameFlags.OneWay, 1, prefix)));
        }
        Parse(Frame(ProtocolV2FrameType.RequestAccepted, ProtocolV2FrameFlags.None, 1, Array.Empty<byte>()));
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.RequestAccepted, ProtocolV2FrameFlags.None, 1, new byte[1])));
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.RequestAccepted, ProtocolV2FrameFlags.OneWay, 1, Array.Empty<byte>())));
    }

    [Test]
    public void StructuralRequestParseRejectsNegativeBudgetNoncanonicalMetadataAndInflatedBody()
    {
        var budget = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(budget.AsSpan(16), -1);
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.HasTimeBudget, 1, budget)));
        var metadata = new byte[19];
        metadata[16] = 0x81; metadata[17] = 0; metadata[18] = 0;
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.HasMetadata, 1, metadata)));
        // count1, key length1, invalid UTF8 key, empty value; byte length4.
        metadata = new byte[21];
        new byte[] { 4, 1, 1, 0xff, 0 }.CopyTo(metadata, 16);
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.HasMetadata, 1, metadata)));
        var compressed = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(compressed.AsSpan(16), (uint)Limits.MaxFramePayloadBytes);
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.Compressed, 1, compressed)));
        BinaryPrimitives.WriteUInt32LittleEndian(compressed.AsSpan(16), 0);
        // Structural parser checks envelope only; it cannot call a provider or business codec.
        Parse(Frame(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.Compressed, 1, compressed));
    }

    [Test]
    public void HandshakeRequestPinsNewOffsetsAndStrictRevisionCapabilitiesAndLengths()
    {
        var request = Offer();
        var writer = new ArrayBufferWriter<byte>();
        ProtocolV2PayloadCodec.WriteHandshakeRequest(writer, request, Limits);
        var bytes = writer.WrittenSpan.ToArray();
        Ensure(bytes.Length == 57);
        Ensure(BinaryPrimitives.ReadUInt16LittleEndian(bytes) == 1);
        Ensure(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(10)) == (ulong)Mandatory);
        Ensure(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(39)) == 4096);
        Ensure(bytes[55] == 0 && bytes[56] == 0);
        var read = ProtocolV2PayloadCodec.ReadHandshakeRequest(new ReadOnlySequence<byte>(bytes), Limits);
        Ensure(read.BindingLimits == Protocol3BindingLimits.Default);
        Parse(Frame(ProtocolV2FrameType.HandshakeRequest, ProtocolV2FrameFlags.None, 0, bytes));
        foreach (var offset in new[] { 0, 53, 54 })
        {
            var invalid = (byte[])bytes.Clone(); invalid[offset] = offset == 0 ? (byte)0 : (byte)1;
            ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.HandshakeRequest, ProtocolV2FrameFlags.None, 0, invalid)));
        }
        var unknownRequired = (byte[])bytes.Clone();
        unknownRequired[10] |= 0x80;
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.HandshakeRequest, ProtocolV2FrameFlags.None, 0, unknownRequired)));
        var missingMandatory = (byte[])bytes.Clone(); missingMandatory[10] &= unchecked((byte)~(byte)ProtocolV2Capabilities.FlowControl);
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.HandshakeRequest, ProtocolV2FrameFlags.None, 0, missingMandatory)));
        var trailing = new byte[bytes.Length + 1]; bytes.CopyTo(trailing, 0);
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.HandshakeRequest, ProtocolV2FrameFlags.None, 0, trailing)));
    }

    [Test]
    public void SuccessHandshakeCarriesExactAuthorityAndNoDowngrade()
    {
        var authority = new Protocol3BindingAuthority(
            new Guid("00112233-4455-6677-8899-aabbccddeeff"),
            new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"), Protocol3BindingLimits.Default);
        var response = new ProtocolV2HandshakeResponse(1, Mandatory, Limits.MaxFramePayloadBytes, 1024, 4096, Authority: authority);
        var writer = new ArrayBufferWriter<byte>();
        ProtocolV2PayloadCodec.WriteHandshakeResponse(writer, response);
        var bytes = writer.WrittenSpan.ToArray();
        Ensure(bytes.Length == 71 && bytes[70] == 0);
        Ensure(bytes.AsSpan(22, 16).SequenceEqual(Convert.FromHexString("33221100554477668899AABBCCDDEEFF")));
        Ensure(ProtocolV2PayloadCodec.ReadHandshakeResponse(new ReadOnlySequence<byte>(bytes), Limits) == response);
        Parse(Frame(ProtocolV2FrameType.HandshakeResponse, ProtocolV2FrameFlags.None, 0, bytes));
        foreach (var offset in new[] { 0, 68, 69, 70 })
        {
            var invalid = (byte[])bytes.Clone(); invalid[offset] = offset == 0 ? (byte)0 : (byte)1;
            ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.HandshakeResponse, ProtocolV2FrameFlags.None, 0, invalid)));
        }
        var emptyAuthority = (byte[])bytes.Clone(); emptyAuthority.AsSpan(22, 16).Clear();
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.HandshakeResponse, ProtocolV2FrameFlags.None, 0, emptyAuthority)));
    }

    [Test]
    public void EveryByteSegmentedFrameHasSameStructuralOutcomeAndPartialFramesDoNotConsume()
    {
        var bytes = Frame(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 1, new byte[] { 2 });
        var first = new Segment(bytes.AsMemory(0, 1));
        var last = first;
        for (var index = 1; index < bytes.Length; index++)
            last = last.Append(bytes.AsMemory(index, 1));
        var sequence = new ReadOnlySequence<byte>(first, 0, last, 1);
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out var header, out _));
        Ensure(sequence.IsEmpty && header.RequestId == 1);
        for (var length = 0; length < bytes.Length; length++)
        {
            sequence = new ReadOnlySequence<byte>(bytes.AsMemory(0, length));
            Ensure(!ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out _, out _));
            Ensure(sequence.Length == length);
        }
    }

    [Test]
    public void PhaseAndDirectionRulesPermitBoundedDrainRefusalButNoTerminalTraffic()
    {
        foreach (var type in new[] { ProtocolV2FrameType.Request, ProtocolV2FrameType.OneWayStreamRequest,
                     ProtocolV2FrameType.BindContract, ProtocolV2FrameType.BindResult,
                     ProtocolV2FrameType.CancelBind, ProtocolV2FrameType.ReleaseBinding,
                     ProtocolV2FrameType.BindingInvalidated, ProtocolV2FrameType.RequestAccepted })
        {
            Ensure(RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Ready, type));
            Ensure(RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Draining, type));
            Ensure(!RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Handshaking, type));
            Ensure(!RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Stopping, type));
            Ensure(!RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Terminal, type));
        }
        Ensure(!RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Draining, ProtocolV2FrameType.HealthCheck));
        Ensure(!RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Draining, ProtocolV2FrameType.ContractManifest));
        Ensure(RpcSessionProtocolRules.IsInboundDirectionAllowed(RpcSessionRole.Server, ProtocolV2FrameType.BindContract));
        Ensure(!RpcSessionProtocolRules.IsInboundDirectionAllowed(RpcSessionRole.Client, ProtocolV2FrameType.BindContract));
        Ensure(RpcSessionProtocolRules.IsInboundDirectionAllowed(RpcSessionRole.Client, ProtocolV2FrameType.RequestAccepted));
        Ensure(!RpcSessionProtocolRules.IsInboundDirectionAllowed(RpcSessionRole.Server, ProtocolV2FrameType.RequestAccepted));
    }

    [Test]
    public void CompressionWalkersUseTypeDependentFixedPrefixBeforeBudgetAndMetadata()
    {
        foreach (var type in new[] { ProtocolV2FrameType.Request, ProtocolV2FrameType.OneWayStreamRequest })
        {
            var fixedPrefix = type == ProtocolV2FrameType.Request ? 16 : 18;
            foreach (var budget in new[] { false, true })
            foreach (var metadata in new[] { false, true })
            {
                var prefix = fixedPrefix + (budget ? 8 : 0) + (metadata ? 2 : 0);
                var bytes = new byte[prefix + 4];
                var flags = ProtocolV2FrameFlags.Compressed;
                if (type == ProtocolV2FrameType.OneWayStreamRequest)
                {
                    flags |= ProtocolV2FrameFlags.OneWay;
                    bytes[16] = 1;
                }
                if (budget) flags |= ProtocolV2FrameFlags.HasTimeBudget;
                if (metadata)
                {
                    flags |= ProtocolV2FrameFlags.HasMetadata;
                    bytes[fixedPrefix + (budget ? 8 : 0)] = 1; // length1, entry count0
                }
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(prefix), 100);
                var payload = new ReadOnlySequence<byte>(bytes);
                Ensure(RpcSession.ReadCompressedOriginalLength(type, flags, payload) == 100);
                Ensure(RpcSession.ReadCompressedDecodedPayloadLength(type, flags, payload) == prefix + 100);
            }
        }
        ExpectViolation(() => RpcSession.ReadCompressedOriginalLength(
            ProtocolV2FrameType.BindContract, ProtocolV2FrameFlags.Compressed, new ReadOnlySequence<byte>(new byte[28])));
    }

    [Test]
    public void OtherControlGenerationDomainsRejectHighBitBeforeComparison()
    {
        var update = new byte[9];
        BinaryPrimitives.WriteUInt64LittleEndian(update, (ulong)long.MaxValue + 1);
        ExpectViolation(() => ProtocolV2PayloadCodec.ReadResponseCompressionPreferenceUpdate(new ReadOnlySequence<byte>(update)));
        ExpectViolation(() => ProtocolV2PayloadCodec.ReadResponseCompressionPreferenceAck(new ReadOnlySequence<byte>(update.AsMemory(0, 8))));
        var refresh = new byte[24];
        Guid.NewGuid().TryWriteBytes(refresh);
        BinaryPrimitives.WriteUInt64LittleEndian(refresh.AsSpan(16), (ulong)long.MaxValue + 1);
        ExpectViolation(() => ProtocolV2PayloadCodec.ReadSessionRefreshRequested(new ReadOnlySequence<byte>(refresh)));
        var manifest = new byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(manifest.AsSpan(8), 1);
        BinaryPrimitives.WriteInt64LittleEndian(manifest.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(manifest.AsSpan(20), 1);
        ExpectViolation(() => Parse(Frame(ProtocolV2FrameType.ContractManifest, ProtocolV2FrameFlags.None, 0, manifest)));
        BinaryPrimitives.WriteInt64LittleEndian(manifest, 1);
        Parse(Frame(ProtocolV2FrameType.ContractManifest, ProtocolV2FrameFlags.None, 0, manifest));
    }

    private static ProtocolV2HandshakeRequest Offer() => new(1, Mandatory, Mandatory,
        Limits.MaxFramePayloadBytes, 1024, 4096, ReadOnlyMemory<byte>.Empty,
        BindingLimits: Protocol3BindingLimits.Default);

    private static byte[] Frame(ProtocolV2FrameType type, ProtocolV2FrameFlags flags, ulong id, byte[] payload)
    {
        var bytes = new byte[15 + payload.Length];
        bytes[0] = 0x8a;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), payload.Length);
        bytes[5] = (byte)type; bytes[6] = (byte)flags;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(7), id);
        payload.CopyTo(bytes, 15);
        return bytes;
    }

    private static void Parse(byte[] bytes)
    {
        var sequence = new ReadOnlySequence<byte>(bytes);
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref sequence, Limits, out _, out _) && sequence.IsEmpty);
    }

    private static void ExpectViolation(Action action)
    {
        try { action(); }
        catch (SharpLinkProtocolViolationException) { return; }
        throw new InvalidOperationException("Expected Protocol3 structural violation.");
    }

    private static void Ensure(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Protocol3 source regression assertion failed.");
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
