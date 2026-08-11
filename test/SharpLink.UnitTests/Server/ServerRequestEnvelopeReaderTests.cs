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
    public async Task ReadShouldPreserveRoutingDeadlineMetadataAndArgumentsAcrossPayloadLayouts()
    {
        var deadline = UtcNow.AddMilliseconds(1_250);
        var metadata = new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "factory-a"),
            new KeyValuePair<string, string>("trace", "42"));
        var arguments = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var payload = CreatePayload(deadline.ToUnixTimeMilliseconds(), metadata, arguments);
        const long monotonicNow = 123_456_789;
        await using var session = CreateSession(ProtocolV2Capabilities.Metadata);

        var contiguous = Read(
            session,
            new ReadOnlySequence<byte>(payload),
            ProtocolV2FrameFlags.HasDeadline | ProtocolV2FrameFlags.HasMetadata,
            MaxMetadataBytes,
            UtcNow,
            monotonicNow);
        var segmented = Read(
            session,
            CreateSegmented(payload, 1),
            ProtocolV2FrameFlags.HasDeadline | ProtocolV2FrameFlags.HasMetadata,
            MaxMetadataBytes,
            UtcNow,
            monotonicNow);

        var expectedTimestamp = monotonicNow +
            (long)Math.Ceiling(1.25 * Stopwatch.Frequency);
        AssertEnvelope(contiguous, deadline, expectedTimestamp, arguments);
        AssertEnvelope(segmented, deadline, expectedTimestamp, arguments);
        Ensure(contiguous.Metadata is { Count: 2 } &&
               contiguous.Metadata[0].Key == "tenant" &&
               contiguous.Metadata[0].Value == "factory-a" &&
               contiguous.Metadata[1].Key == "trace" &&
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
    [Arguments(0)]
    [Arguments(sizeof(long))]
    [Arguments((sizeof(long) * 2) - 1)]
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
        Ensure(exception.Message == "Request routing prefix is truncated.",
            $"routing prefix error message for {payloadBytes} bytes");
    }

    [Test]
    public async Task ReadShouldRejectTruncatedDeadline()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.None);
        var payload = CreateRoutingPayload(new byte[sizeof(long) - 1]);

        var exception = CaptureSharpLinkException(() => Read(
            session,
            new ReadOnlySequence<byte>(payload),
            ProtocolV2FrameFlags.HasDeadline,
            MaxMetadataBytes,
            UtcNow,
            1));

        Ensure(exception.Code == SharpLinkErrorCode.ProtocolViolation,
            "truncated deadline error code");
        Ensure(exception.Message == "Request deadline is truncated.",
            "truncated deadline error message");
    }

    [Test]
    public async Task ReadShouldRejectDeadlineOutsideSupportedUtcRange()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.None);
        var payload = CreatePayload(long.MaxValue, metadata: null, arguments: []);

        var exception = CaptureSharpLinkException(() => Read(
            session,
            new ReadOnlySequence<byte>(payload),
            ProtocolV2FrameFlags.HasDeadline,
            MaxMetadataBytes,
            UtcNow,
            1));

        Ensure(exception.Code == SharpLinkErrorCode.ProtocolViolation,
            "out-of-range deadline error code");
        Ensure(exception.Message == "Request deadline is outside the supported UTC range.",
            "out-of-range deadline error message");
        Ensure(exception.InnerException is ArgumentOutOfRangeException,
            "out-of-range deadline should retain the conversion failure");
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

        Ensure(exception.Code == SharpLinkErrorCode.ProtocolViolation,
            "metadata negotiation error code");
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

        Ensure(exception.Code == SharpLinkErrorCode.ProtocolViolation,
            $"{shape} metadata error code");
        Ensure(exception.Message == "Request metadata length is invalid.",
            $"{shape} metadata error message");
    }

    [Test]
    public async Task ReadShouldUseProvidedMonotonicTimeForExpiredDeadline()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.None);
        const long monotonicNow = 987_654_321;
        var deadline = UtcNow.AddMilliseconds(-1);
        var payload = CreatePayload(deadline.ToUnixTimeMilliseconds(), metadata: null, arguments: []);

        var envelope = Read(
            session,
            new ReadOnlySequence<byte>(payload),
            ProtocolV2FrameFlags.HasDeadline,
            MaxMetadataBytes,
            UtcNow,
            monotonicNow);

        Ensure(envelope.Deadline == deadline, "expired UTC deadline");
        Ensure(envelope.RpcDeadline.Timestamp == monotonicNow,
            "expired deadline must use the caller-provided monotonic timestamp");
    }

    [Test]
    public async Task ReadShouldSaturateAnExtremeFutureDeadline()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.None);
        var deadline = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
        const long monotonicNow = long.MaxValue - 1;
        var payload = CreatePayload(deadline.ToUnixTimeMilliseconds(), metadata: null, arguments: []);

        var envelope = Read(
            session,
            new ReadOnlySequence<byte>(payload),
            ProtocolV2FrameFlags.HasDeadline,
            MaxMetadataBytes,
            DateTimeOffset.MinValue,
            monotonicNow);

        Ensure(envelope.Deadline == deadline, "extreme UTC deadline");
        Ensure(envelope.RpcDeadline.Timestamp == long.MaxValue,
            "extreme deadline must saturate instead of overflowing");
    }

    [Test]
    public async Task ReadSteadyStateWithoutOptionalFieldsShouldAllocateNothing()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.None);
        var payload = CreatePayload(deadlineMilliseconds: null, metadata: null, arguments: [1, 2, 3, 4]);
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
        DateTimeOffset expectedDeadline,
        long expectedDeadlineTimestamp,
        byte[] expectedArguments)
    {
        Ensure(envelope.InterfaceHash == InterfaceHash, "interface hash");
        Ensure(envelope.MethodHash == MethodHash, "method hash");
        Ensure(envelope.Deadline == expectedDeadline, "deadline");
        Ensure(envelope.RpcDeadline.Timestamp == expectedDeadlineTimestamp,
            "deterministic monotonic deadline");
        Ensure(envelope.Arguments.ToArray().AsSpan().SequenceEqual(expectedArguments),
            "arguments must remain byte-for-byte intact");
    }

    private static byte[] CreatePayload(
        long? deadlineMilliseconds,
        SharpLinkMetadata? metadata,
        byte[] arguments)
    {
        var writer = new ArrayBufferWriter<byte>();
        var routing = writer.GetSpan(sizeof(long) * 2);
        BinaryPrimitives.WriteInt64LittleEndian(routing, InterfaceHash);
        BinaryPrimitives.WriteInt64LittleEndian(routing[sizeof(long)..], MethodHash);
        writer.Advance(sizeof(long) * 2);
        if (deadlineMilliseconds is { } deadline)
        {
            var deadlineBytes = writer.GetSpan(sizeof(long));
            BinaryPrimitives.WriteInt64LittleEndian(deadlineBytes, deadline);
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
        var payload = new byte[(sizeof(long) * 2) + tail.Length];
        BinaryPrimitives.WriteInt64LittleEndian(payload, InterfaceHash);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(sizeof(long)), MethodHash);
        tail.CopyTo(payload, sizeof(long) * 2);
        return payload;
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
        public BufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public void SetNext(BufferSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset utcNow,
        long timestamp) : TimeProvider
    {
        public override long TimestampFrequency => Stopwatch.Frequency;
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long GetTimestamp() => timestamp;
    }
}
