using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Server;

public sealed class DecodedRequestMetadataReuseTests
{
    private static readonly TimeProvider Clock = new FixedClock();

    [Test]
    public async Task DecodedEnvelopeShouldMatchOriginalParserAcrossBoundaries()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.Metadata);
        // Retains the independent corpus from the performance study. Every second
        // parse below compares the original parser with the production ReadDecoded.
        Ensure(Validate(session) == 2924, "the full differential corpus must run");
    }

    [Test]
    public async Task ReuseShouldStillRequireNegotiatedMetadata()
    {
        await using var sourceSession = CreateSession(ProtocolV2Capabilities.Metadata);
        await using var destinationSession = CreateSession(ProtocolV2Capabilities.None);
        var flags = ProtocolV2FrameFlags.Compressed | ProtocolV2FrameFlags.HasMetadata;
        var encoded = new ReadOnlySequence<byte>(Build(1, false, 20));
        var decoded = new ReadOnlySequence<byte>(Build(1, false, 64));
        var first = ServerRequestEnvelopeReader.Read(sourceSession, encoded, flags, 65536, Clock);
        var expected = Outcome(() => ServerRequestEnvelopeReader.Read(
            destinationSession, decoded, flags, 65536, Clock, first.RpcDeadline));
        var actual = Outcome(() => ServerRequestEnvelopeReader.ReadDecoded(
            destinationSession, decoded, encoded, in first, flags, 65536, Clock));
        Ensure(expected.StartsWith("error:", StringComparison.Ordinal), "missing capability must fail");
        Ensure(expected == actual, "fallback must preserve the capability error");
    }

    private static RpcSession CreateSession(ProtocolV2Capabilities capabilities)
    {
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "metadata-reuse", new Pipe().Reader, new Pipe().Writer,
            RpcSessionTestFixture.ServerOptions(), completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, capabilities);
        return session;
    }

    private static int Validate(RpcSession session)
    {
        var count = 0;
        foreach (var metadataEntries in new[] { 0, 1, 8 })
        {
            foreach (var timed in new[] { false, true })
            {
                var flags = (metadataEntries > 0 ? ProtocolV2FrameFlags.HasMetadata : 0) |
                            (timed ? ProtocolV2FrameFlags.HasTimeBudget : 0);
                var bytes = Build(metadataEntries, timed, 8);
                var whole = ServerRequestEnvelopeReader.Read(
                    session, new ReadOnlySequence<byte>(bytes), flags, 65536, Clock);
                for (var length = 0; length <= bytes.Length; length++)
                {
                    var cropped = bytes[..length];
                    foreach (var split in new[] { -1, 0, length / 2, length })
                    {
                        var sequence = split < 0
                            ? new ReadOnlySequence<byte>(cropped)
                            : Split(cropped, split);
                        var expected = Outcome(() => ServerRequestEnvelopeReader.Read(
                            session, sequence, flags, 65536, Clock, whole.RpcDeadline));
                        var actual = Outcome(() => ServerRequestEnvelopeReader.ReadDecoded(
                            session,
                            sequence,
                            new ReadOnlySequence<byte>(bytes),
                            in whole,
                            flags,
                            65536,
                            Clock));
                        Ensure(expected == actual, "decoded/plain/optional/truncated differential outcomes");
                        count++;
                    }
                }

                var encoded = Build(metadataEntries, timed, 20);
                var decoded = Build(metadataEntries, timed, 64);
                var original = ServerRequestEnvelopeReader.Read(
                    session, new ReadOnlySequence<byte>(encoded), flags, 65536, Clock);
                for (var split = -1; split < Math.Min(encoded.Length, 40); split++)
                {
                    var encodedSequence = split < 0
                        ? new ReadOnlySequence<byte>(encoded)
                        : Split(encoded, split);
                    var expected = Outcome(() => ServerRequestEnvelopeReader.Read(
                        session,
                        new ReadOnlySequence<byte>(decoded),
                        flags,
                        65536,
                        Clock,
                        original.RpcDeadline));
                    var actual = Outcome(() => ServerRequestEnvelopeReader.ReadDecoded(
                        session,
                        new ReadOnlySequence<byte>(decoded),
                        encodedSequence,
                        in original,
                        flags,
                        65536,
                        Clock));
                    Ensure(expected == actual, "decoded rebind across source layouts");
                    count++;
                }

                // Mutated routing, budget or metadata must fall back to the exact old parser.
                var prefixLength = encoded.Length - (int)original.Arguments.Length;
                for (var offset = 0; offset < prefixLength; offset++)
                {
                    var old = decoded[offset];
                    decoded[offset] ^= 0xff;
                    var expected = Outcome(() => ServerRequestEnvelopeReader.Read(
                        session,
                        new ReadOnlySequence<byte>(decoded),
                        flags,
                        65536,
                        Clock,
                        original.RpcDeadline));
                    var actual = Outcome(() => ServerRequestEnvelopeReader.ReadDecoded(
                        session,
                        new ReadOnlySequence<byte>(decoded),
                        new ReadOnlySequence<byte>(encoded),
                        in original,
                        flags,
                        65536,
                        Clock));
                    Ensure(expected == actual, "changed-prefix fallback");
                    count++;
                    decoded[offset] = old;
                }

                if (metadataEntries > 0)
                {
                    var limitedExpected = Outcome(() => ServerRequestEnvelopeReader.Read(
                        session,
                        new ReadOnlySequence<byte>(decoded),
                        flags,
                        0,
                        Clock,
                        original.RpcDeadline));
                    var limitedActual = Outcome(() => ServerRequestEnvelopeReader.ReadDecoded(
                        session,
                        new ReadOnlySequence<byte>(decoded),
                        new ReadOnlySequence<byte>(encoded),
                        in original,
                        flags,
                        0,
                        Clock));
                    Ensure(limitedExpected == limitedActual, "new metadata limit must still apply");
                    count++;

                    var reused = ServerRequestEnvelopeReader.ReadDecoded(
                        session,
                        new ReadOnlySequence<byte>(decoded),
                        new ReadOnlySequence<byte>(encoded),
                        in original,
                        flags,
                        65536,
                        Clock);
                    Ensure(ReferenceEquals(reused.Metadata, original.Metadata),
                        "must reuse already immutable metadata");
                    Ensure(reused.RpcDeadline.Equals(original.RpcDeadline),
                        "must retain exact deadline");
                    decoded[^1] = 55;
                    Ensure(reused.Arguments.ToArray()[^1] == 55,
                        "arguments must alias decoded owner, not encoded owner");
                    count += 3;
                }
            }
        }

        return count;
    }

    private static string Outcome(Func<ServerRequestEnvelope> action)
    {
        try
        {
            var envelope = action();
            return $"{envelope.InterfaceHash}:{envelope.MethodHash}:{envelope.RpcDeadline.Timestamp}:" +
                   string.Join(";", envelope.Metadata?.Select(entry => $"{entry.Key}={entry.Value}") ?? []) +
                   ":" + Convert.ToHexString(envelope.Arguments.ToArray());
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType()}:{(exception as SharpLinkException)?.Code}:{exception.Message}";
        }
    }

    private static byte[] Build(int entries, bool timed, int argumentBytes)
    {
        var writer = new ArrayBufferWriter<byte>();
        var prefix = writer.GetSpan(16);
        BinaryPrimitives.WriteInt64LittleEndian(prefix, 123);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[8..], 456);
        writer.Advance(16);
        if (timed)
        {
            prefix = writer.GetSpan(8);
            BinaryPrimitives.WriteInt64LittleEndian(prefix, TimeSpan.TicksPerSecond);
            writer.Advance(8);
        }

        if (entries > 0)
        {
            var pairs = Enumerable.Range(0, entries)
                .Select(index => new KeyValuePair<string, string>(
                    "key" + index,
                    "tenant-value-" + index))
                .ToArray();
            var metadataWriter = new ArrayBufferWriter<byte>();
            ProtocolV2PayloadCodec.WriteMetadata(metadataWriter, new SharpLinkMetadata(pairs));
            ProtocolV2PayloadCodec.WriteVarUInt32(writer, (uint)metadataWriter.WrittenCount);
            writer.Write(metadataWriter.WrittenSpan);
        }

        writer.Write(new byte[argumentBytes]);
        return writer.WrittenSpan.ToArray();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static ReadOnlySequence<byte> Split(byte[] bytes, int at)
    {
        var first = new Segment(bytes.AsMemory(0, at));
        var last = first.Append(bytes.AsMemory(at));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new Segment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };
            Next = segment;
            return segment;
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override long TimestampFrequency => 1_000_000_000;
        public override long GetTimestamp() => 123456;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
