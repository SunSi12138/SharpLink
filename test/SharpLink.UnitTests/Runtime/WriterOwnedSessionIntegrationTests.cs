using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;

namespace SharpLink.UnitTests.Runtime;

// These checks exercise the existing generated protocol bridge, real RpcSession and SendPump.
// The connection explicitly selects the new sender; ordinary fixtures remain on the legacy path.
public sealed class WriterOwnedSessionIntegrationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GeneratedStreamMustEmitValuesThenExactlyOneTerminal(bool sized)
    {
        await using var fixture = new Fixture();
        var codec = new IntCodec(sized);
        await fixture.Pump(73, Values(7, 8, 9), codec).WaitAsync(Limit);
        var frames = await fixture.ReadThroughTerminal();
        Ensure(frames.Count == 4 && frames.Take(3).All(f => f.Type == ProtocolV2FrameType.StreamData), "DATA count");
        Ensure(frames.Take(3).Select(f => BinaryPrimitives.ReadInt32LittleEndian(f.Payload.AsSpan(2)))
            .SequenceEqual(new[] { 7, 8, 9 }), "wire values/order changed");
        Ensure(frames.All(f => f.RequestId == 73 && BinaryPrimitives.ReadUInt16LittleEndian(f.Payload) == 0), "stream identity changed");
        Ensure(frames[^1].Flags == ProtocolV2FrameFlags.None, "clean EOF was not clean");
        Ensure(fixture.Session.WriterOwnedStreams is not null && fixture.Session.WriterOwnedStreams.PreparedBytes == 0,
            "normal generated bridge did not settle its B3 preparation");
        Ensure(codec.SizeQueries == (sized ? 3 : 0), "exact-size selection was not honored");
    }

    [Test]
    public async Task ExhaustedCreditMustKeepDataAndEofOrderedUntilRealWindowUpdate()
    {
        await using var fixture = new Fixture(window: 4);
        var pump = fixture.Pump(73, Values(7, 8, 9), new IntCodec(false));
        var first = await fixture.ReadBatch();
        Ensure(first.Count == 1 && first[0].Type == ProtocolV2FrameType.StreamData, "EOF or second DATA bypassed credit");
        Ensure(!pump.IsCompleted && fixture.Session.WriterOwnedStreams!.ConnectionCredit == 0, "stream did not wait on credit");
        fixture.Credit(73, 4);
        var second = await fixture.ReadBatch();
        Ensure(second.Count == 1 && BinaryPrimitives.ReadInt32LittleEndian(second[0].Payload.AsSpan(2)) == 8,
            "second DATA did not use the real session WindowUpdate");
        fixture.Credit(73, 4);
        await pump.WaitAsync(Limit);
        var last = await fixture.ReadThroughTerminal();
        Ensure(last.Count == 2 && last[0].Type == ProtocolV2FrameType.StreamData &&
            last[1].Type == ProtocolV2FrameType.StreamComplete, "EOF overtook the last queued DATA");
    }

    [Test]
    public async Task BusinessFailureMustKeepAlreadyAcceptedDataBeforeMappedError()
    {
        await using var fixture = new Fixture();
        var expected = new InvalidOperationException("business failure");
        var failure = await Capture(fixture.Pump(73, ValueThenThrow(expected), new IntCodec(false)));
        Ensure(ReferenceEquals(failure, expected), "business exception identity changed");
        await fixture.Session.SendStreamErrorWithWriterAsync(73, 0,
            new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "mapped failure", expected),
            CancellationToken.None).AsTask().WaitAsync(Limit);
        var frames = await fixture.ReadThroughTerminal();
        Ensure(frames.Count == 2 && frames[0].Type == ProtocolV2FrameType.StreamData &&
            frames[1].Type == ProtocolV2FrameType.StreamComplete && (frames[1].Flags & ProtocolV2FrameFlags.Error) != 0,
            "mapped terminal lost accepted DATA ordering");
    }

    [Test]
    public async Task CodecFailureMustNotPublishPartialData()
    {
        await using var fixture = new Fixture();
        var codec = new IntCodec(false) { Failure = new IOException("codec failure") };
        var failure = await Capture(fixture.Pump(73, Values(7), codec));
        Ensure(ReferenceEquals(failure, codec.Failure), "codec exception identity changed");
        await fixture.Session.SendStreamErrorWithWriterAsync(73, 0,
            new SharpLinkException(SharpLinkErrorCode.Internal, "mapped codec failure"), CancellationToken.None)
            .AsTask().WaitAsync(Limit);
        var frames = await fixture.ReadThroughTerminal();
        Ensure(frames.Count == 1 && (frames[0].Flags & ProtocolV2FrameFlags.Error) != 0, "partial DATA escaped serialization");
    }

    [Test]
    public async Task EmptyStreamMustCompleteWithoutConsumingFlowCredit()
    {
        await using var fixture = new Fixture(window: 1);
        await fixture.Pump(73, Values(), new IntCodec(false)).WaitAsync(Limit);
        var frames = await fixture.ReadThroughTerminal();
        Ensure(frames.Count == 1 && frames[0].Type == ProtocolV2FrameType.StreamComplete, "empty stream EOF count");
        Ensure(fixture.Session.WriterOwnedStreams!.ConnectionCredit == 1, "EOF consumed DATA credit");
    }

    [Test]
    public async Task ZeroLengthItemMustStillConsumeOneCreditByte()
    {
        await using var fixture = new Fixture(window: 1);
        var pump = fixture.Pump(73, Values(1, 2), new IntCodec(false) { Empty = true });
        var first = await fixture.ReadBatch();
        Ensure(first.Count == 1 && first[0].Payload.Length == 2 && !pump.IsCompleted, "zero-length item bypassed credit");
        fixture.Credit(73, 1);
        await pump.WaitAsync(Limit);
        var last = await fixture.ReadThroughTerminal();
        Ensure(last.Count == 2 && last[0].Payload.Length == 2, "empty representation did not resume");
    }

    [Test]
    public async Task RemoteCancelMustUnblockPreparedStreamWithoutFaultingConnection()
    {
        await using var fixture = new Fixture(window: 4);
        var pump = fixture.Pump(73, Values(7, 8, 9), new IntCodec(false));
        _ = await fixture.ReadBatch();
        var expected = new SharpLinkException(SharpLinkErrorCode.Cancelled, "remote canceled");
        fixture.Session.AbortSendStreams(73, expected);
        var failure = await Capture(pump);
        Ensure(ReferenceEquals(failure, expected),
            "remote abort did not reach the actual producer/drain; " +
            $"received={failure.GetType().Name}: {failure.Message}, " +
            $"connected={fixture.Session.IsConnected}, " +
            $"prepared={fixture.Session.WriterOwnedStreams?.PreparedBytes}, " +
            $"retained={fixture.Session.WriterOwnedStreams?.RetainedStreams}");
        Ensure(fixture.Session.IsConnected && fixture.Session.WriterOwnedStreams!.PreparedBytes == 0,
            "stream cancellation faulted the connection or leaked preparation");
    }

    [Test]
    public async Task IndependentStreamsMustShareOneConnectionWindow()
    {
        await using var fixture = new Fixture(window: 4);
        var a = fixture.Pump(71, Values(1), new IntCodec(false));
        var first = await fixture.ReadThroughTerminal();
        await a.WaitAsync(Limit);
        Ensure(first.Count == 2, "first stream did not complete");
        var b = fixture.Pump(72, Values(2), new IntCodec(false));
        Ensure(!b.IsCompleted, "second stream received an independent connection window");
        fixture.Credit(71, 4);
        await b.WaitAsync(Limit);
        var second = await fixture.ReadThroughTerminal();
        Ensure(second.Count == 2 && second.All(f => f.RequestId == 72), "cross-stream credit did not resume the waiting sender");
    }

    [Test]
    public async Task LegacyCreditAndRawDataMustNotBypassWriterAuthority()
    {
        await using var fixture = new Fixture();
        var rejected = false;
        try { await fixture.Session.AcquireStreamSendCreditAsync(73, 0, 1, CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        Ensure(rejected && fixture.Session.WriterOwnedStreams is null, "legacy sender created another authority");
        var packet = fixture.Session.RentFrameWriter();
        using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, 73))
        {
            packet.Write(new byte[] { 0, 0, 1 });
        }
        rejected = false;
        try { fixture.Session.SendPacket(packet); }
        catch (InvalidOperationException) { rejected = true; }
        Ensure(rejected, "raw DATA bypassed ordered B3 admission");
        await fixture.Pump(74, Values(9), new IntCodec(false)).WaitAsync(Limit);
        var frames = await fixture.ReadThroughTerminal();
        Ensure(frames.All(f => f.RequestId == 74), "rejected raw frame reached transport");
    }

    [Test]
    public async Task UnsolicitedUpdateMustNotAllocateSenderState()
    {
        await using var fixture = new Fixture();
        fixture.Credit(99, 4);
        Ensure(fixture.Session.WriterOwnedStreams is null, "unknown update allocated a sender");
    }

    [Test]
    public async Task WithoutNegotiatedFlowControlSelectedModeMustRemainLegacy()
    {
        await using var fixture = new Fixture(flowControl: false);
        Ensure(!fixture.Session.UsesWriterOwnedStreams, "unnegotiated flow selected B3");
        await fixture.Pump(73, Values(1), new IntCodec(false)).WaitAsync(Limit);
        var frames = await fixture.ReadThroughTerminal();
        Ensure(frames.Count == 2 && fixture.Session.WriterOwnedStreams is null, "unnegotiated fallback failed");
    }

    [Test]
    public async Task SessionShutdownMustUnblockPreparedDataAndTerminal()
    {
        await using var fixture = new Fixture(window: 4);
        var pump = fixture.Pump(73, Values(7, 8, 9), new IntCodec(false));
        _ = await fixture.ReadBatch();
        fixture.Session.BeginShutdown();
        var failure = await Capture(pump);
        Ensure(failure is SharpLinkException or OperationCanceledException, "shutdown did not complete the producer");
        Ensure(fixture.Session.WriterOwnedStreams!.PreparedBytes == 0, "shutdown retained unadmitted frames");
    }

    [Test]
    public async Task ClientRoleFenceMustKeepQueuedRequestBeforeReadyData()
    {
        await using var fixture = new Fixture(role: RpcSessionRole.Client);
        var request = fixture.Session.RentFrameWriter();
        using (request.BeginPacketScope(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.None, 73))
            request.Write(new byte[ProtocolV2Constants.RequestPrefixBytes]);
        fixture.Session.SendPacket(request);
        // Exercise the same session sender with the client role. This verifies its durable
        // Request fence, not the higher-level ClientConnection producer adapter.
        await fixture.Pump(73, Values(7, 8), new IntCodec(false)).WaitAsync(Limit);
        var frames = await fixture.ReadThroughTerminal();
        Ensure(frames.Count == 4 && frames[0].Type == ProtocolV2FrameType.Request &&
            frames[1].Type == ProtocolV2FrameType.StreamData && frames[^1].Type == ProtocolV2FrameType.StreamComplete,
            "ready DATA overtook the ordinary Request");
    }

    [Test]
    public async Task CompressedFramesMustDebitOriginalBytesNotWireBytes()
    {
        await using var fixture = new Fixture(window: 2048, compression: true);
        var codec = new IntCodec(false) { RepeatedBytes = 2048 };
        var pump = fixture.Pump(73, Values(7, 8), codec);
        var first = await fixture.ReadBatch();
        Ensure(first.Count == 1 && (first[0].Flags & ProtocolV2FrameFlags.Compressed) != 0,
            "actual outbound compression was not used");
        Ensure(first[0].WireBytes < 2048 && first[0].Payload.Length == 2050,
            "compressed frame did not preserve decoded payload length");
        Ensure(!pump.IsCompleted && fixture.Session.WriterOwnedStreams!.ConnectionCredit == 0,
            "compressed wire length was incorrectly used as flow credit");
        fixture.Credit(73, 2048);
        await pump.WaitAsync(Limit);
        var last = await fixture.ReadThroughTerminal();
        Ensure(last.Count == 2 && last[0].Payload.AsSpan(2).ToArray().All(b => b == 8),
            "compressed resumed item did not round-trip");
    }

    private static async Task<Exception> Capture(Task task)
    {
        try { await task.WaitAsync(Limit); }
        catch (Exception error) when (error is not TimeoutException) { return error; }
        throw new Exception("expected operation failure");
    }

    private static async IAsyncEnumerable<int> Values(params int[] values)
    {
        foreach (var value in values) { yield return value; await Task.Yield(); }
    }

    private static async IAsyncEnumerable<int> ValueThenThrow(Exception error)
    {
        yield return 7;
        await Task.Yield();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class IntCodec(bool sized) : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        internal int SizeQueries;
        internal bool Empty;
        internal int RepeatedBytes;
        internal Exception? Failure;
        public bool CanExactSize => sized;
        public void Serialize(in int value, IBufferWriter<byte> writer)
        {
            if (Failure is { } failure) throw failure;
            if (Empty) return;
            if (RepeatedBytes != 0)
            {
                writer.GetSpan(RepeatedBytes)[..RepeatedBytes].Fill((byte)value);
                writer.Advance(RepeatedBytes);
                return;
            }
            BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value);
            writer.Advance(4);
        }
        public int Deserialize(in ReadOnlySequence<byte> bytes) => BinaryPrimitives.ReadInt32LittleEndian(bytes.FirstSpan);
        public bool TryGetEncodedSize(in int value, out int size) { size = 4; return true; }
        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        { SizeQueries++; size = 4; snapshot = null; return true; }
        public void SerializeSized(in int value, IBufferWriter<byte> writer, int size, IRpcSizedCodecSnapshot? snapshot)
            => Serialize(value, writer);
        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot) { }
    }

    private readonly record struct WireFrame(ProtocolV2FrameType Type, ProtocolV2FrameFlags Flags,
        ulong RequestId, byte[] Payload, int WireBytes);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SharpLinkRuntimeContext _context;
        private readonly Pipe _input = new();
        private readonly Pipe _output = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        internal RpcSession Session { get; }
        internal Fixture(int window = 1024, bool flowControl = true, bool compression = false,
            RpcSessionRole role = RpcSessionRole.Server)
        {
            var builder = new SharpLinkRuntimeContextBuilder();
            if (compression) builder.Configure(options => options.Compression.Providers.Add(new TestCompressionProvider()));
            _context = builder.Build(includeGeneratedAssemblyCatalog: false);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport("writer-owned-session",
                _input.Reader, _output.Writer,
                new RpcSessionCreationOptions(role, _context, writerOwnedStreams: true), completeHandshake: false);
            var capabilities = flowControl ? ProtocolV2Capabilities.FlowControl : ProtocolV2Capabilities.None;
            if (compression) capabilities |= ProtocolV2Capabilities.Compression;
            RpcSessionTestFixture.CompleteHandshake(Session, capabilities,
                streamReceiveWindowBytes: window, connectionReceiveWindowBytes: window,
                compressionBinding: compression ? _context.Compression.ProviderBindings[0] : null);
        }
        internal Task Pump(long requestId, IAsyncEnumerable<int> values, IRpcCodec<int> codec)
            => new RpcSessionGeneratedServerBridge(Session).PumpOutboundStreamAsync(requestId, 0,
                values, codec, false, 101, 202, CancellationToken.None).AsTask();
        internal void Credit(long requestId, int bytes)
            => Session.ApplyWindowUpdate(requestId, new ProtocolV2WindowUpdate(0, checked((uint)bytes)));
        internal async Task<List<WireFrame>> ReadBatch()
        {
            var read = await _output.Reader.ReadAsync().AsTask().WaitAsync(Limit);
            var remaining = read.Buffer;
            var frames = new List<WireFrame>();
            try
            {
                while (ProtocolV2FrameParser.TryReadFrame(ref remaining, _context.Protocol, out var header, out var payload))
                {
                    var wireBytes = checked((int)payload.Length + ProtocolV2Constants.HeaderBytes);
                    var decoded = Session.DecodeInboundPayload(header.Type, header.Flags, payload,
                        CancellationToken.None, out var owner);
                    try { frames.Add(new WireFrame(header.Type, header.Flags, header.RequestId, decoded.ToArray(), wireBytes)); }
                    finally { Session.ReturnDecodedPayload(owner); }
                }
                Ensure(remaining.IsEmpty && frames.Count != 0, "output contained an incomplete frame");
                return frames;
            }
            finally { _output.Reader.AdvanceTo(read.Buffer.End); }
        }
        internal async Task<List<WireFrame>> ReadThroughTerminal()
        {
            var frames = new List<WireFrame>();
            do { frames.AddRange(await ReadBatch()); }
            while (frames[^1].Type != ProtocolV2FrameType.StreamComplete);
            return frames;
        }
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await _output.Reader.CompleteAsync();
            await _input.Writer.CompleteAsync();
            _context.Dispose();
        }
    }
}
