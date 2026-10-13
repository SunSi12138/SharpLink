using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Server;

public sealed class ServerRequestEnvelopeReaderTests
{
    private const long InterfaceHash = 0x112233445566778;
    private const long MethodHash = 0x776655443322110;
    private const int MaxMetadataBytes = 1024;
    private static readonly DateTimeOffset UtcNow =
        DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    [Test]
    public async Task ReadShouldResolveTimeBudgetOnceAndPreserveMetadataAndArgumentsAcrossLayouts()
    {
        var budget = TimeSpan.FromMilliseconds(1_250);
        var metadata = new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "factory-a"),
            new KeyValuePair<string, string>("trace", "42"));
        var arguments = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var payload = CreatePayload(budget.Ticks, metadata, arguments);
        const long monotonicNow = 123_456_789;
        await using var session = CreateSession(ProtocolV2Capabilities.Metadata);

        var contiguous = Read(
            session,
            new ReadOnlySequence<byte>(payload),
            ProtocolV2FrameFlags.HasTimeBudget | ProtocolV2FrameFlags.HasMetadata,
            MaxMetadataBytes,
            UtcNow,
            monotonicNow);
        var segmented = Read(
            session,
            CreateSegmented(payload, 1),
            ProtocolV2FrameFlags.HasTimeBudget | ProtocolV2FrameFlags.HasMetadata,
            MaxMetadataBytes,
            UtcNow,
            monotonicNow);

        var expectedTimestamp = SharpLinkTime.AddDuration(
            monotonicNow, budget, Stopwatch.Frequency);
        AssertEnvelope(contiguous, expectedTimestamp, arguments);
        AssertEnvelope(segmented, expectedTimestamp, arguments);
        Ensure(contiguous.Metadata is { Count: 2 } &&
               contiguous.Metadata[0].Value == "factory-a" &&
               contiguous.Metadata[1].Value == "42",
            "contiguous metadata values");
        Ensure(segmented.Metadata is { Count: 2 } &&
               segmented.Metadata[0].Value == "factory-a" &&
               segmented.Metadata[1].Value == "42",
            "segmented metadata values");

        payload[^1] = 0x7A;
        Ensure(contiguous.Arguments.ToArray()[^1] == 0x7A &&
               segmented.Arguments.ToArray()[^1] == 0x7A,
            "arguments must remain slices over the original payload instead of being copied");
    }

    [Test]
    public async Task ReparseShouldConsumeWireBudgetWithoutRestartingResolvedDeadline()
    {
        var budget = TimeSpan.FromSeconds(5);
        var payload = CreatePayload(budget.Ticks, metadata: null, arguments: [1, 2, 3]);
        await using var session = CreateSession(ProtocolV2Capabilities.None);
        var firstProvider = new FixedTimeProvider(UtcNow, 1_000);
        var first = ServerRequestEnvelopeReader.Read(
            session,
            new ReadOnlySequence<byte>(payload),
            ProtocolV2FrameFlags.HasTimeBudget,
            MaxMetadataBytes,
            firstProvider);
        var laterProvider = new FixedTimeProvider(UtcNow.AddSeconds(4), 9_000_000);

        var reparsed = ServerRequestEnvelopeReader.Read(
            session,
            new ReadOnlySequence<byte>(payload),
            ProtocolV2FrameFlags.HasTimeBudget,
            MaxMetadataBytes,
            laterProvider,
            first.RpcDeadline);

        Ensure(reparsed.RpcDeadline.Timestamp == first.RpcDeadline.Timestamp,
            "reparse must retain the original process-local monotonic boundary");
        Ensure(reparsed.Arguments.ToArray().AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }),
            "reparse still consumes the TimeBudget field before exposing arguments");
    }

    [Test]
    public async Task ReceiptPrefixMustRemainOutsideMetadataAndDecodedArguments()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.Metadata);
        var hash = ServerRequestTestWire.Hash;
        var receipt = new ContractRegistrationReceipt(Guid.NewGuid(), Guid.NewGuid(), 7,
            InterfaceHash, hash, Guid.NewGuid());
        var identity = new ProtocolV2RequestIdentity(InterfaceHash, MethodHash, hash,
            RpcMethodKind.Unary, 0, receipt, RpcRegistrationRequirement.ExactRegistration);
        var writer = new ArrayBufferWriter<byte>();
        ProtocolV2RequestIdentityCodec.Write(writer, in identity);
        BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(sizeof(long)), TimeSpan.TicksPerSecond);
        writer.Advance(sizeof(long));
        using var metadataWriter = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteMetadata(metadataWriter,
            new SharpLinkMetadata(new KeyValuePair<string, string>("tenant", "receipt-owner")));
        ProtocolV2PayloadCodec.WriteVarUInt32(writer, checked((uint)metadataWriter.WrittenCount));
        writer.Write(metadataWriter.WrittenSpan);
        var prefix = writer.WrittenSpan.ToArray();
        writer.Write(new byte[] { 1, 2, 3 });
        var encoded = writer.WrittenSpan.ToArray();
        var decoded = new byte[prefix.Length + 128];
        prefix.CopyTo(decoded, 0);
        decoded[^1] = 71;
        var clock = new FixedTimeProvider(UtcNow, 123);
        var flags = ProtocolV2FrameFlags.HasTimeBudget | ProtocolV2FrameFlags.HasMetadata;
        var original = ServerRequestEnvelopeReader.Read(session, new(encoded), flags, MaxMetadataBytes, clock);
        var rebound = ServerRequestEnvelopeReader.ReadDecoded(session, new(decoded), new(encoded),
            in original, flags, MaxMetadataBytes, clock);

        Ensure(rebound.Identity == identity, "receipt and requested registration requirement remain exact");
        Ensure(ReferenceEquals(rebound.Metadata, original.Metadata),
            "metadata reuse begins after the variable-length receipt prefix");
        Ensure(rebound.Arguments.Length == 128 && rebound.Arguments.ToArray()[^1] == 71,
            "decoded arguments must not include receipt, budget or metadata bytes");
        var segmented = ServerRequestEnvelopeReader.Read(session, CreateSegmented(encoded, 1),
            flags, MaxMetadataBytes, clock);
        Ensure(segmented.Identity == identity && segmented.Metadata is { Count: 1 },
            "fragmented receipt prefix parses before metadata without business decoding");
    }

    [Test]
    [Arguments(0)]
    [Arguments(sizeof(long))]
    [Arguments((sizeof(long) * 2) - 1)]
    [Arguments(ProtocolV2Constants.RequestPrefixBytes - 1)]
    public async Task ReadShouldRejectEveryTruncatedRoutingPrefix(int payloadBytes)
    {
        await using var session = CreateSession(ProtocolV2Capabilities.None);
        var exception = CaptureSharpLinkException(() => Read(
            session,
            new ReadOnlySequence<byte>(new byte[payloadBytes]),
            ProtocolV2FrameFlags.None,
            MaxMetadataBytes,
            UtcNow,
            1));
        Ensure(exception.Code == SharpLinkErrorCode.ProtocolViolation,
            $"routing prefix error code for {payloadBytes} bytes");
        Ensure(exception.Message == "Request identity prefix is truncated.",
            $"routing prefix error message for {payloadBytes} bytes");
    }

    [Test]
    public async Task ReadShouldRejectTruncatedTimeBudget()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.None);
        var payload = CreateRoutingPayload(new byte[sizeof(long) - 1]);
        var exception = CaptureSharpLinkException(() => Read(
            session,
            new ReadOnlySequence<byte>(payload),
            ProtocolV2FrameFlags.HasTimeBudget,
            MaxMetadataBytes,
            UtcNow,
            1));
        Ensure(exception.Message == "Request time budget is truncated.",
            "truncated TimeBudget message");
    }

    [Test]
    public async Task ReadShouldRejectNegativeTimeBudget()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.None);
        var payload = CreatePayload(-1, metadata: null, arguments: []);
        var exception = CaptureSharpLinkException(() => Read(
            session,
            new ReadOnlySequence<byte>(payload),
            ProtocolV2FrameFlags.HasTimeBudget,
            MaxMetadataBytes,
            UtcNow,
            1));
        Ensure(exception.Message == "Request time budget cannot be negative.",
            "negative TimeBudget message");
    }

    [Test]
    public async Task ZeroTimeBudgetShouldResolveToCurrentMonotonicTimestamp()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.None);
        const long monotonicNow = 987_654_321;
        var envelope = Read(
            session,
            new ReadOnlySequence<byte>(CreatePayload(0, null, [])),
            ProtocolV2FrameFlags.HasTimeBudget,
            MaxMetadataBytes,
            UtcNow,
            monotonicNow);
        Ensure(envelope.RpcDeadline.Timestamp == monotonicNow,
            "zero TimeBudget must be immediately expired on the local monotonic clock");
    }

    [Test]
    public async Task ReadShouldRejectMetadataWhenCapabilityWasNotNegotiated()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.None);
        var payload = CreateRoutingPayload([0]);
        var exception = CaptureSharpLinkException(() => Read(
            session,
            new ReadOnlySequence<byte>(payload),
            ProtocolV2FrameFlags.HasMetadata,
            MaxMetadataBytes,
            UtcNow,
            1));
        Ensure(exception.Message == "Request metadata was not negotiated during handshake.",
            "metadata negotiation error message");
    }

    [Test]
    [Arguments("over_limit")]
    [Arguments("truncated_payload")]
    [Arguments("truncated_varint")]
    public async Task ReadShouldRejectInvalidMetadataLength(string shape)
    {
        await using var session = CreateSession(ProtocolV2Capabilities.Metadata);
        var tail = shape switch
        {
            "over_limit" => new byte[] { 5, 1, 2, 3, 4, 5 },
            "truncated_payload" => new byte[] { 5, 1 },
            "truncated_varint" => new byte[] { 0x80 },
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        var maxMetadataBytes = shape == "over_limit" ? 4 : MaxMetadataBytes;
        var exception = CaptureSharpLinkException(() => Read(
            session,
            new ReadOnlySequence<byte>(CreateRoutingPayload(tail)),
            ProtocolV2FrameFlags.HasMetadata,
            maxMetadataBytes,
            UtcNow,
            1));
        Ensure(exception.Message == "Request metadata length is invalid.",
            $"{shape} metadata error message");
    }

    [Test]
    public async Task ReadSteadyStateWithoutOptionalFieldsShouldAllocateNothing()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.None);
        var payload = CreatePayload(timeBudgetTicks: null, metadata: null, arguments: [1, 2, 3, 4]);
        var sequence = new ReadOnlySequence<byte>(payload);
        var timeProvider = new FixedTimeProvider(UtcNow, timestamp: 1);
        _ = ReadBatch(session, sequence, timeProvider, 100_000);
        _ = GC.GetAllocatedBytesForCurrentThread();

        const int iterations = 20_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = ReadBatch(session, sequence, timeProvider, iterations);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Ensure(allocated == 0,
            $"steady-state envelope parsing allocated {allocated} bytes over {iterations} calls");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ReadBatch(
        RpcSession session,
        ReadOnlySequence<byte> sequence,
        TimeProvider timeProvider,
        int iterations)
    {
        long checksum = 0;
        for (var index = 0; index < iterations; index++)
        {
            var envelope = ServerRequestEnvelopeReader.Read(
                session, sequence, ProtocolV2FrameFlags.None, 1, timeProvider);
            checksum += envelope.InterfaceHash + envelope.Arguments.Length;
        }
        return checksum;
    }

    private static void AssertEnvelope(
        ServerRequestEnvelope envelope,
        long expectedDeadlineTimestamp,
        byte[] expectedArguments)
    {
        Ensure(envelope.InterfaceHash == InterfaceHash, "interface hash");
        Ensure(envelope.MethodHash == MethodHash, "method hash");
        Ensure(envelope.RpcDeadline.Timestamp == expectedDeadlineTimestamp,
            "deterministic monotonic deadline");
        Ensure(envelope.Arguments.ToArray().AsSpan().SequenceEqual(expectedArguments),
            "arguments must remain byte-for-byte intact");
    }

    private static byte[] CreatePayload(
        long? timeBudgetTicks,
        SharpLinkMetadata? metadata,
        byte[] arguments)
    {
        var writer = new ArrayBufferWriter<byte>();
        ServerRequestTestWire.WriteIdentity(writer, InterfaceHash, MethodHash);
        if (timeBudgetTicks is { } budget)
        {
            var budgetBytes = writer.GetSpan(sizeof(long));
            BinaryPrimitives.WriteInt64LittleEndian(budgetBytes, budget);
            writer.Advance(sizeof(long));
        }
        if (metadata is not null)
        {
            using var metadataWriter = new PooledByteBufferWriter();
            ProtocolV2PayloadCodec.WriteMetadata(metadataWriter, metadata);
            ProtocolV2PayloadCodec.WriteVarUInt32(writer, checked((uint)metadataWriter.WrittenCount));
            writer.Write(metadataWriter.WrittenSpan);
        }
        writer.Write(arguments);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] CreateRoutingPayload(byte[] tail)
    {
        var writer = new ArrayBufferWriter<byte>();
        ServerRequestTestWire.WriteIdentity(writer, InterfaceHash, MethodHash);
        writer.Write(tail);
        return writer.WrittenSpan.ToArray();
    }

    private static ServerRequestEnvelope Read(
        RpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags,
        int maxMetadataBytes,
        DateTimeOffset utcNow,
        long monotonicNow)
        => ServerRequestEnvelopeReader.Read(
            session,
            payload,
            flags,
            maxMetadataBytes,
            new FixedTimeProvider(utcNow, monotonicNow));

    private static RpcSession CreateSession(ProtocolV2Capabilities capabilities)
    {
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "envelope-reader",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions(),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, capabilities);
        return session;
    }

    private static SharpLinkException CaptureSharpLinkException(Action action)
    {
        try
        {
            action();
            throw new Exception("Expected SharpLinkException.");
        }
        catch (SharpLinkException exception)
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
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;
        public void SetNext(BufferSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow, long timestamp) : TimeProvider
    {
        public override long TimestampFrequency => Stopwatch.Frequency;
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long GetTimestamp() => timestamp;
    }
}
