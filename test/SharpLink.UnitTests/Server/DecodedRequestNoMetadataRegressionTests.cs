using System.Buffers.Binary;
using System.IO.Pipelines;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Server;

public sealed class DecodedRequestNoMetadataRegressionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PlainSecondParseShouldKeepDeadlineAndUseDecodedArguments(bool timed)
    {
        var clock = new CountingClock();
        await using var session = CreateSession();
        var flags = ProtocolV2FrameFlags.Compressed |
            (timed ? ProtocolV2FrameFlags.HasTimeBudget : 0);
        var encoded = Build(timed, 20);
        var decoded = Build(timed, 128);
        var original = ServerRequestEnvelopeReader.Read(session, new(encoded), flags, 0, clock);
        var reads = clock.Reads;
        clock.Now += TimeSpan.TicksPerSecond;
        var rebound = ServerRequestEnvelopeReader.Read(
            session, new(decoded), flags, 0, clock, original.RpcDeadline);
        Ensure(rebound.Metadata is null, "no metadata snapshot is created");
        Ensure(rebound.InterfaceHash == original.InterfaceHash && rebound.MethodHash == original.MethodHash,
            "routing must be unchanged");
        Ensure(rebound.RpcDeadline.Equals(original.RpcDeadline), "the first exact deadline must remain");
        Ensure(clock.Reads == reads, "rebind must not reset the deadline from a new timestamp");
        decoded[^1] = 71;
        Ensure(rebound.Arguments.Length == 128 && rebound.Arguments.ToArray()[^1] == 71,
            "arguments belong to the decoded owner");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NoMetadataFallbackShouldMatchParserAcrossLayouts(bool timed)
    {
        await using var session = CreateSession();
        var clock = new CountingClock();
        var flags = ProtocolV2FrameFlags.Compressed |
            (timed ? ProtocolV2FrameFlags.HasTimeBudget : 0);
        var encoded = Build(timed, 20);
        var decoded = Build(timed, 64);
        var original = ServerRequestEnvelopeReader.Read(session, new(encoded), flags, 0, clock);
        for (var length = 0; length <= decoded.Length; length++)
        {
            var cropped = decoded[..length];
            for (var split = -1; split <= length; split++)
            {
                var sequence = split < 0 ? new ReadOnlySequence<byte>(cropped) : Split(cropped, split);
                Check(sequence, new(encoded), original);
            }
        }
        for (var split = 0; split <= encoded.Length; split++)
            Check(new(decoded), Split(encoded, split), original);
        var prefixLength = timed ? 24 : 16;
        for (var offset = 0; offset < prefixLength; offset++)
        {
            decoded[offset] ^= 0xff;
            Check(new(decoded), new(encoded), original);
            decoded[offset] ^= 0xff;
        }
        // Inconsistent provenance must not select the fixed-prefix rebind path.
        Check(new(decoded), new(encoded), original with { Arguments = new ReadOnlySequence<byte>(new byte[1]) });

        void Check(ReadOnlySequence<byte> output, ReadOnlySequence<byte> input, ServerRequestEnvelope first)
        {
            var expected = Outcome(() => ServerRequestEnvelopeReader.Read(session, output, flags, 0, clock, first.RpcDeadline));
            var actual = Outcome(() => ServerRequestEnvelopeReader.ReadDecoded(session, output, input, in first, flags, 0, clock));
            Ensure(expected == actual, "plain rebind must retain exact parser results and errors");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RealDecompressionWithoutMetadataShouldKeepOriginalParsingSemantics(bool timed)
    {
        var provider = new TestCompressionProvider();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Compression.Providers.Add(provider))
            .Build(includeGeneratedAssemblyCatalog: false);
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "plain-inline-decode", new Pipe().Reader, new Pipe().Writer,
            RpcSessionTestFixture.ServerOptions(context), completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, ProtocolV2Capabilities.Compression,
            compressionBinding: context.Compression.ProviderBindings[0]);
        var flags = ProtocolV2FrameFlags.Compressed |
            (timed ? ProtocolV2FrameFlags.HasTimeBudget : 0);
        var source = Enumerable.Repeat((byte)37, 512).ToArray();
        using var wire = new PooledByteBufferWriter();
        wire.Write(Build(timed, 0));
        var length = wire.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)source.Length);
        wire.Advance(4);
        Ensure(provider.TryCompress(new(source), wire, 1024), "test compression should succeed");
        var encoded = new ReadOnlySequence<byte>(wire.WrittenMemory);
        var clock = new CountingClock();
        var first = ServerRequestEnvelopeReader.Read(session, encoded, flags, 0, clock);
        var decoded = session.DecodeInboundPayload(ProtocolV2FrameType.Request, flags, encoded,
            CancellationToken.None, out var decodedOwner);
        try
        {
            var rebound = ServerRequestEnvelopeReader.Read(session, decoded,
                flags, 0, clock, first.RpcDeadline);
            Ensure(rebound.Arguments.ToArray().AsSpan().SequenceEqual(source), "decoded payload bytes");
            Ensure(rebound.RpcDeadline.Equals(first.RpcDeadline), "deadline remains anchored at first parse");
        }
        finally
        {
            session.ReturnDecodedPayload(decodedOwner);
        }
    }

    private static RpcSession CreateSession()
        => RpcSessionTestFixture.CreateSessionOverTestTransport(
            "no-metadata-rebind", new Pipe().Reader, new Pipe().Writer, RpcSessionTestFixture.ServerOptions());

    private static byte[] Build(bool timed, int arguments)
    {
        var bytes = new byte[(timed ? 24 : 16) + arguments];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, 123);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), 456);
        if (timed) BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), TimeSpan.TicksPerSecond);
        return bytes;
    }

    private static string Outcome(Func<ServerRequestEnvelope> parse)
    {
        try
        {
            var e = parse();
            return $"{e.InterfaceHash}:{e.MethodHash}:{e.RpcDeadline.Timestamp}:" + Convert.ToHexString(e.Arguments.ToArray());
        }
        catch (Exception e)
        {
            return $"{e.GetType()}:{(e as SharpLinkException)?.Code}:{e.Message}";
        }
    }

    private static ReadOnlySequence<byte> Split(byte[] bytes, int at)
    {
        var first = new Segment(bytes.AsMemory(0, at));
        var last = first.Append(bytes.AsMemory(at));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal Segment Append(ReadOnlyMemory<byte> next)
        {
            var segment = new Segment(next) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }

    private sealed class CountingClock : TimeProvider
    {
        internal long Now = 123456;
        internal int Reads;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { Reads++; return Now; }
    }
}
