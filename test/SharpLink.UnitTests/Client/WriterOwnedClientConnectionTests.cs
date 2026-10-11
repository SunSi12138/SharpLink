using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class WriterOwnedClientConnectionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ActualClientProducerMustUseOrderedWriterLaneAndHoistCodec(bool sized)
    {
        await using var fixture = new Fixture(window: 32);
        var codec = new Codec(sized);
        await fixture.Connection.SendClientStreamAsync(fixture.RequestId, 0, Values(7, 8, 9),
            codec, fixture.ProducerToken).WaitAsync(Limit);
        var frames = await fixture.ReadThroughTerminal();
        Ensure(frames.Count == 5 && frames[0].Type == ProtocolV2FrameType.Request &&
            frames[^1].Type == ProtocolV2FrameType.StreamComplete, "Request/DATA/EOF count or order");
        Ensure(frames.Skip(1).Take(3).Select(f => BinaryPrimitives.ReadInt32LittleEndian(f.Payload.AsSpan(2)))
            .SequenceEqual(new[] { 7, 8, 9 }), "client values/order changed");
        Ensure(codec.CapabilityReads == 1 && codec.SizeQueries == (sized ? 3 : 0) && codec.Serializations == 3,
            "actual ClientConnection did not hoist the capability or serialized an item twice");
        Ensure(fixture.Connection.Session.WriterOwnedStreams is not null &&
            fixture.Connection.Session.WriterOwnedStreams.PreparedBytes == 0, "actual client used legacy sending or leaked preparation");
    }

    [Test]
    public async Task ActualClientProducerMustResumeFromSessionWindowUpdate()
    {
        await using var fixture = new Fixture(window: 4);
        var send = fixture.Connection.SendClientStreamAsync(fixture.RequestId, 0,
            Values(7, 8), new Codec(false), fixture.ProducerToken);
        var first = await fixture.ReadThroughData();
        Ensure(first.Count(f => f.Type == ProtocolV2FrameType.StreamData) == 1 && !send.IsCompleted,
            "second item or terminal bypassed connection credit");
        fixture.Connection.Session.ApplyWindowUpdate(fixture.RequestId, new ProtocolV2WindowUpdate(0, 4));
        await send.WaitAsync(Limit);
        var last = await fixture.ReadThroughTerminal();
        Ensure(last.Count == 2 && last[0].Type == ProtocolV2FrameType.StreamData &&
            last[1].Type == ProtocolV2FrameType.StreamComplete, "blocked client stream did not preserve its terminal order");
    }

    [Test]
    public async Task PendingCallCancellationMustCleanActualClientPreparation()
    {
        await using var fixture = new Fixture(window: 4);
        var codec = new Codec(false);
        var send = fixture.Connection.SendClientStreamAsync(fixture.RequestId, 0,
            Values(7, 8, 9), codec, fixture.ProducerToken);
        _ = await fixture.ReadThroughData();
        await codec.ThreeSerialized.Task.WaitAsync(Limit);
        fixture.Cancel.Cancel();
        var failure = await Capture(send);
        Ensure(failure is OperationCanceledException or SharpLinkException,
            "logical call cancellation did not reach the stream producer");
        var sender = fixture.Connection.Session.WriterOwnedStreams!;
        var connected = fixture.Connection.Session.IsConnected;
        var prepared = sender.PreparedBytes;
        Ensure(connected && prepared == 0,
            $"call cancellation leaked queued preparation or closed the physical connection; " +
            $"connected={connected}, prepared={prepared}, retained={sender.RetainedStreams}, " +
            $"cleanup={sender.CleanupFailure?.GetType().Name ?? "none"}");
    }

    [Test]
    public async Task ActualClientBusinessFailureMustFollowItsAcceptedData()
    {
        await using var fixture = new Fixture(window: 32);
        var expected = new IOException("client iterator failed");
        var failure = await Capture(fixture.Connection.SendClientStreamAsync(fixture.RequestId, 0,
            ValueThenFailure(expected), new Codec(false), fixture.ProducerToken));
        Ensure(ReferenceEquals(failure, expected), "client iterator failure identity changed");
        var frames = await fixture.ReadThroughTerminal();
        Ensure(frames.Count == 3 && frames[0].Type == ProtocolV2FrameType.Request &&
            frames[1].Type == ProtocolV2FrameType.StreamData &&
            (frames[2].Flags & ProtocolV2FrameFlags.Error) != 0, "client error terminal overtook DATA");
    }

    [Test]
    public async Task EmptyClientStreamMustStillFenceRequestAndPublishEof()
    {
        await using var fixture = new Fixture(window: 1);
        await fixture.Connection.SendClientStreamAsync(fixture.RequestId, 0, Values(),
            new Codec(false), fixture.ProducerToken).WaitAsync(Limit);
        var frames = await fixture.ReadThroughTerminal();
        Ensure(frames.Count == 2 && frames[0].Type == ProtocolV2FrameType.Request &&
            frames[1].Type == ProtocolV2FrameType.StreamComplete, "empty client stream lost Request predecessor");
        Ensure(fixture.Connection.Session.WriterOwnedStreams!.ConnectionCredit == 1, "client EOF consumed credit");
    }

    private static async Task<Exception> Capture(Task task)
    {
        try { await task.WaitAsync(Limit); }
        catch (Exception error) when (error is not TimeoutException) { return error; }
        throw new Exception("expected failure");
    }

    private static async IAsyncEnumerable<int> Values(params int[] values)
    {
        foreach (var value in values) { yield return value; await Task.Yield(); }
    }

    private static async IAsyncEnumerable<int> ValueThenFailure(Exception error)
    {
        yield return 7;
        await Task.Yield();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Codec(bool sized) : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        internal int CapabilityReads, SizeQueries, Serializations;
        internal readonly TaskCompletionSource ThreeSerialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CanExactSize { get { CapabilityReads++; return sized; } }
        public void Serialize(in int value, IBufferWriter<byte> writer)
        {
            BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value);
            writer.Advance(4);
            if (++Serializations == 3) ThreeSerialized.TrySetResult();
        }
        public int Deserialize(in ReadOnlySequence<byte> bytes) => BinaryPrimitives.ReadInt32LittleEndian(bytes.FirstSpan);
        public bool TryGetEncodedSize(in int value, out int size) { size = 4; return true; }
        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        { SizeQueries++; size = 4; snapshot = null; return true; }
        public void SerializeSized(in int value, IBufferWriter<byte> writer, int size, IRpcSizedCodecSnapshot? snapshot)
            => Serialize(value, writer);
        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot) { }
    }

    private readonly record struct Frame(ProtocolV2FrameType Type, ProtocolV2FrameFlags Flags, byte[] Payload);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SharpLinkClient _owner;
        private readonly Pipe _input = new();
        private readonly Pipe _output = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        private readonly CancellationTokenSource _connectionCancel = new();
        private readonly Task _pendingOutcome;
        internal readonly CancellationTokenSource Cancel = new();
        internal ClientConnection Connection { get; }
        internal long RequestId { get; }
        internal CancellationToken ProducerToken { get; }
        internal Fixture(int window)
        {
            _owner = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
            var context = (SharpLinkRuntimeContext)_owner.RuntimeContext;
            var session = RpcSessionTestFixture.CreateSessionOverTestTransport("writer-owned-client",
                _input.Reader, _output.Writer,
                new RpcSessionCreationOptions(RpcSessionRole.Client, context, writerOwnedStreams: true), completeHandshake: false);
            RpcSessionTestFixture.CompleteHandshake(session, ProtocolV2Capabilities.FlowControl,
                streamReceiveWindowBytes: window, connectionReceiveWindowBytes: window);
            Connection = new ClientConnection(_owner, session, _connectionCancel, 8, context);
            var operation = Connection.PendingCalls.Rent(context.Codecs.GetCodec<int>(), PendingCallKind.ClientStreaming,
                default, Cancel.Token, out var requestId, hasResponsePayload: true, responseNullable: false);
            RequestId = requestId;
            ProducerToken = Connection.PendingCalls.GetProducerCancellationToken(requestId);
            _pendingOutcome = operation.AsValueTask().AsTask();
            var request = session.RentFrameWriter();
            using (request.BeginPacketScope(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.None, unchecked((ulong)requestId)))
                request.Write(new byte[ProtocolV2Constants.RequestPrefixBytes]);
            // Wire ordering fixture: PendingRequestTable and ClientConnection are real; no fake
            // remote service completes the Request. Disposal observes the pending outcome once.
            session.SendPacket(request);
        }
        private async Task<List<Frame>> ReadBatch()
        {
            var read = await _output.Reader.ReadAsync().AsTask().WaitAsync(Limit);
            var remaining = read.Buffer;
            var frames = new List<Frame>();
            try
            {
                while (ProtocolV2FrameParser.TryReadFrame(ref remaining, Connection.Session.RuntimeContext.Protocol,
                    out var header, out var payload))
                {
                    Ensure(header.RequestId == unchecked((ulong)RequestId), "client frame changed request identity");
                    frames.Add(new Frame(header.Type, header.Flags, payload.ToArray()));
                }
                Ensure(remaining.IsEmpty && frames.Count != 0, "incomplete client output");
                return frames;
            }
            finally { _output.Reader.AdvanceTo(read.Buffer.End); }
        }
        internal async Task<List<Frame>> ReadThroughData()
        {
            var frames = new List<Frame>();
            do { frames.AddRange(await ReadBatch()); }
            while (!frames.Any(f => f.Type == ProtocolV2FrameType.StreamData));
            return frames;
        }
        internal async Task<List<Frame>> ReadThroughTerminal()
        {
            var frames = new List<Frame>();
            do { frames.AddRange(await ReadBatch()); }
            while (frames[^1].Type != ProtocolV2FrameType.StreamComplete);
            return frames;
        }
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            try { await _pendingOutcome.WaitAsync(Limit); }
            catch (Exception error) when (error is SharpLinkException or OperationCanceledException) { }
            Cancel.Dispose();
            await _output.Reader.CompleteAsync();
            await _input.Writer.CompleteAsync();
            await _owner.DisposeAsync();
        }
    }
}
