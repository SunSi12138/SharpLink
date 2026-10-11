using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public sealed class WriterOwnedDeadlineIntegrationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SerializationCrossingDeadlineMustNotPublishData(bool sized)
    {
        await using var fixture = new Fixture();
        var clock = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), clock);
        var codec = new Codec(sized, () => clock.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(2)));
        using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(fixture.Session.Id, null, deadline, clock)))
        {
            var error = await Capture(fixture.Pump(new ControlledStream(1), codec));
            Ensure(error is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "deadline publication guard failed");
        }
        await fixture.Session.FlushSendQueueAsync().AsTask().WaitAsync(Limit);
        Ensure(!fixture.Output.Reader.TryRead(out var result) || result.Buffer.IsEmpty, "expired serialized DATA reached the transport");
        if (!result.Buffer.IsEmpty) fixture.Output.Reader.AdvanceTo(result.Buffer.End);
        Ensure(fixture.Session.WriterOwnedStreams!.PreparedBytes == 0, "expired serialization retained preparation");
    }

    [Test]
    public async Task DeadlineDuringEofDrainMustNotWaitForNonCooperativeDispose()
    {
        await using var fixture = new Fixture();
        var clock = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), clock);
        var stream = new ControlledStream(2) { BlockDispose = true };
        Task pump;
        using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(fixture.Session.Id, null, deadline, clock)))
            pump = fixture.Pump(stream, new Codec(false));
        try
        {
            Ensure(stream.EofObserved && clock.ActiveTimerCount > 0, "fixture did not reach a timed EOF drain");
            var first = await fixture.Output.Reader.ReadAsync().AsTask().WaitAsync(Limit);
            fixture.Output.Reader.AdvanceTo(first.Buffer.End);
            clock.Advance(TimeSpan.FromSeconds(1));
            var error = await Capture(pump);
            Ensure(error is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "EOF drain did not report deadline");
            Ensure(stream.DisposeEntered && !stream.Disposed.Task.IsCompleted, "producer waited for non-cooperative disposal");
            Ensure(fixture.Session.WriterOwnedStreams!.PreparedBytes == 0, "deadline left unadmitted DATA/EOF buffers");
        }
        finally { stream.Disposed.TrySetResult(); }
    }

    [Test]
    public async Task CallCancellationMustCleanPreparationWhileMoveNextIgnoresIt()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var stream = new ControlledStream(2) { BlockBeforeEof = true };
        var pump = fixture.Pump(stream, new Codec(false), cancellation.Token);
        await stream.WaitingForEof.Task.WaitAsync(Limit);
        var first = await fixture.Output.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        fixture.Output.Reader.AdvanceTo(first.Buffer.End);
        cancellation.Cancel();
        try
        {
            Ensure(!pump.IsCompleted, "the deliberately blocked user MoveNext was not exercised");
            Ensure(fixture.Session.WriterOwnedStreams!.PreparedBytes == 0,
                "call cancellation waited for user code before cleaning prepared DATA");
        }
        finally { stream.EofGate.TrySetResult(false); }
        Ensure(await Capture(pump) is OperationCanceledException, "canceled producer did not finish with cancellation");
    }

    [Test]
    public async Task CancellationDuringFirstSerializationMustRetireItsUnreturnedHandle()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var error = await Capture(fixture.Pump(new ControlledStream(1),
            new Codec(false, cancellation.Cancel), cancellation.Token));
        Ensure(error is OperationCanceledException, "first serialization cancellation was lost");
        var owner = fixture.Session.WriterOwnedStreams!;
        await fixture.Session.DisposeAsync();
        Ensure(owner.PreparedBytes == 0 && owner.RetainedStreams == 0, "first-send cancellation leaked its lifecycle");
    }

    [Test]
    public async Task ServiceDeadlineErrorBeforeFrozenDeadlineMustRemainABusinessFailure()
    {
        await using var fixture = new Fixture();
        var clock = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromHours(1), clock);
        var expected = new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "service-level deadline");
        var stream = new ControlledStream(1) { EofFailure = expected };
        using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(fixture.Session.Id, null, deadline, clock)))
        {
            Ensure(ReferenceEquals(await Capture(fixture.Pump(stream, new Codec(false))), expected), "service exception changed");
            await fixture.Session.SendStreamErrorWithWriterAsync(73, 0, expected, CancellationToken.None)
                .AsTask().WaitAsync(Limit);
        }
        var read = await fixture.Output.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        var remaining = read.Buffer;
        var types = new List<ProtocolV2FrameType>();
        while (ProtocolV2FrameParser.TryReadFrame(ref remaining, fixture.Session.RuntimeContext.Protocol, out var header, out _))
            types.Add(header.Type);
        fixture.Output.Reader.AdvanceTo(read.Buffer.End);
        Ensure(types.Count == 2 && types[0] == ProtocolV2FrameType.StreamData && types[1] == ProtocolV2FrameType.StreamComplete,
            "a business error code was mistaken for the framework deadline and discarded accepted DATA");
    }

    private static async Task<Exception> Capture(Task task)
    {
        try { await task.WaitAsync(Limit); }
        catch (Exception error) when (error is not TimeoutException) { return error; }
        throw new Exception("expected failure");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class ControlledStream(int items) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _index;
        internal bool BlockBeforeEof, BlockDispose, EofObserved, DisposeEntered;
        internal Exception? EofFailure;
        internal readonly TaskCompletionSource WaitingForEof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> EofGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => _index;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            if (_index++ < items) return new(true);
            EofObserved = true;
            if (EofFailure is { } failure) return ValueTask.FromException<bool>(failure);
            if (!BlockBeforeEof) return new(false);
            WaitingForEof.TrySetResult();
            return new(EofGate.Task);
        }
        public ValueTask DisposeAsync()
        {
            DisposeEntered = true;
            return BlockDispose ? new(Disposed.Task) : ValueTask.CompletedTask;
        }
    }

    private sealed class Codec(bool sized, Action? serialized = null) : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        public bool CanExactSize => sized;
        public void Serialize(in int value, IBufferWriter<byte> writer)
        {
            BitConverter.TryWriteBytes(writer.GetSpan(4), value);
            writer.Advance(4);
            serialized?.Invoke();
        }
        public int Deserialize(in ReadOnlySequence<byte> bytes) => BitConverter.ToInt32(bytes.FirstSpan);
        public bool TryGetEncodedSize(in int value, out int size) { size = 4; return true; }
        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        { size = 4; snapshot = null; return true; }
        public void SerializeSized(in int value, IBufferWriter<byte> writer, int size, IRpcSizedCodecSnapshot? snapshot)
            => Serialize(value, writer);
        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot) { }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        internal readonly Pipe Output = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        internal readonly RpcSession Session;
        internal Fixture()
        {
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport("writer-owned-deadline", _input.Reader,
                Output.Writer, new RpcSessionCreationOptions(RpcSessionRole.Server,
                    RpcSessionTestFixture.RuntimeContext, writerOwnedStreams: true), completeHandshake: false);
            RpcSessionTestFixture.CompleteHandshake(Session, ProtocolV2Capabilities.FlowControl,
                streamReceiveWindowBytes: 4, connectionReceiveWindowBytes: 4);
        }
        internal Task Pump(IAsyncEnumerable<int> stream, IRpcCodec<int> codec, CancellationToken token = default)
            => new RpcSessionGeneratedServerBridge(Session).PumpOutboundStreamAsync(73, 0,
                stream, codec, false, 101, 202, token).AsTask();
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Output.Reader.CompleteAsync();
            await _input.Writer.CompleteAsync();
        }
    }
}
