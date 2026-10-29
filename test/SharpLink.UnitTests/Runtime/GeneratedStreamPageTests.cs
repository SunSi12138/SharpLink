using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SharpLink.UnitTests.Runtime;

public class GeneratedStreamPageTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task FirstItemShouldPublishWhileNextMoveNextRemainsBlocked()
    {
        await using var fixture = new Fixture();
        var stream = new ControlledStream();
        var pump = fixture.Pump(stream);
        try
        {
            await stream.SecondMoveEntered.Task.WaitAsync(Timeout);
            await fixture.Transport.DirectWriteEntered.Task.WaitAsync(Timeout);
            Ensure(!pump.IsCompleted, "second MoveNext must still be pending when the first item is published");
            var frames = fixture.Frames();
            Ensure(frames.Count == 1 && frames[0].Type == ProtocolV2FrameType.StreamData,
                "a ready item must not wait for the service's next item or EOF to fill a page");
            Ensure(BinaryPrimitives.ReadInt32LittleEndian(frames[0].Payload.AsSpan(sizeof(ushort))) == 37,
                "the independently published first item must preserve its bytes");
        }
        finally
        {
            stream.Finish.TrySetResult(false);
            await pump.WaitAsync(Timeout);
        }
        await fixture.Flush();
        Ensure(fixture.Frames().Count == 2, "clean EOF must append exactly one terminal");
        Ensure(fixture.Session.EncodedStreamPageBytesOwned == 0,
            "completed production and physical output must release the page budget");
    }

    [Test]
    public async Task LaterMoveNextFailureShouldNotRetractPublishedFirstItem()
    {
        await using var fixture = new Fixture();
        var stream = new ControlledStream();
        var pump = fixture.Pump(stream);
        await stream.SecondMoveEntered.Task.WaitAsync(Timeout);
        await fixture.Transport.DirectWriteEntered.Task.WaitAsync(Timeout);
        var failure = new InvalidOperationException("second MoveNext failed");
        stream.Finish.TrySetException(failure);
        await ExpectSameFailure(pump, failure);
        await fixture.Flush();
        var frames = fixture.Frames();
        Ensure(frames.Count == 1 && frames[0].Type == ProtocolV2FrameType.StreamData,
            "later business failure must preserve the first item and must not publish a success terminal");
        Ensure(fixture.Session.EncodedStreamPageBytesOwned == 0,
            "failed enumeration must release its producer reference after the first frame drains");
    }

    [Test]
    public async Task NormalCompletionShouldUseTheSamePageAsItsDataFrames()
    {
        await using var fixture = new Fixture();
        await fixture.Pump(new ValuesStream(11, 22, 33)).WaitAsync(Timeout);
        await fixture.Flush();
        var writes = fixture.Transport.Snapshot();
        var pages = writes.Where(static write => write.PageArray is not null).ToArray();
        Ensure(pages.Length > 0, "eligible built-in primitives must use encoded-page transport");
        Ensure(writes.All(static write => write.PageArray is not null),
            "both data and the normal terminal must avoid writer-backed fallback");
        Ensure(pages.All(write => ReferenceEquals(write.PageArray, pages[0].PageArray)),
            "small data frames and normal StreamComplete must share one physical page");
        var frames = fixture.Frames();
        Ensure(frames.Count == 4 && frames[^1].Type == ProtocolV2FrameType.StreamComplete &&
            frames[^1].Flags == ProtocolV2FrameFlags.None,
            "three values must be followed by exactly one non-error completion");
        for (var index = 0; index < 3; index++)
            Ensure(BinaryPrimitives.ReadInt32LittleEndian(frames[index].Payload.AsSpan(sizeof(ushort))) == 11 * (index + 1),
                "page append must not overwrite previously published payloads");
        Ensure(fixture.Session.EncodedStreamPageBytesOwned == 0, "all page references must drain after flush");
    }

    [Test]
    public async Task InvocationCancellationMustNotReturnPageStillInPhysicalWrite()
    {
        await using var fixture = new Fixture(holdDirectWrite: true);
        using var cancellation = new CancellationTokenSource();
        var stream = new ControlledStream();
        var pump = fixture.Pump(stream, cancellationToken: cancellation.Token);
        try
        {
            await stream.SecondMoveEntered.Task.WaitAsync(Timeout);
            await fixture.Transport.DirectWriteEntered.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            await ExpectCancellation(pump);
            Ensure(fixture.Session.EncodedStreamPageBytesOwned > 0,
                "logical invocation cancellation must leave a physical writer's page reservation alive");
            Ensure(fixture.Transport.HeldMemory.Span.SequenceEqual(fixture.Transport.Snapshot()[0].Bytes),
                "a canceled producer must not corrupt memory still borrowed by transport");
        }
        finally
        {
            fixture.Transport.ReleaseDirectWrite.TrySetResult();
            stream.Finish.TrySetResult(false);
        }
        await fixture.Flush();
        Ensure(fixture.Session.EncodedStreamPageBytesOwned == 0,
            "the final physical write completion, not cancellation, must restore page capacity");
    }

    [Test]
    public async Task DeadlineMustRetainProducerPageUntilUncooperativeMoveNextPhysicallyEnds()
    {
        await using var fixture = new Fixture();
        var timeProvider = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
        var stream = new ControlledStream(honorCancellation: false);
        Task? cleanup = null;
        using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
            fixture.Session.Id, authentication: null, deadline, timeProvider)))
        {
            var pump = fixture.Pump(stream);
            try
            {
                await stream.SecondMoveEntered.Task.WaitAsync(Timeout);
                await fixture.Transport.DirectWriteEntered.Task.WaitAsync(Timeout);
                await fixture.Flush();
                timeProvider.Advance(TimeSpan.FromSeconds(1));
                try
                {
                    await pump.WaitAsync(Timeout);
                    throw new Exception("expected generated stream deadline");
                }
                catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DeadlineExceeded)
                {
                    cleanup = SharpLinkPhysicalWork.GetAbandonedUserWork(exception)
                        ?? throw new Exception("deadline did not retain physical producer cleanup");
                }
                Ensure(cleanup is not null && !cleanup.IsCompleted && fixture.Session.EncodedStreamPageBytesOwned > 0,
                    "logical deadline must retain producer P while the uncooperative MoveNext still owns its lifetime");
                Ensure(fixture.Frames().Count == 1, "deadline must not manufacture a success StreamComplete");
            }
            finally
            {
                stream.Finish.TrySetResult(false);
                if (cleanup is not null) await cleanup.WaitAsync(Timeout);
            }
        }
        Ensure(fixture.Session.EncodedStreamPageBytesOwned == 0,
            "physical enumerator completion and disposal must release the last producer page reference");
    }

    [Test]
    public async Task ExhaustedPageBudgetShouldUseWriterFallbackWithoutWaiting()
    {
        await using var fixture = new Fixture();
        var budget = new EncodedStreamPageBudget(4096, size => new byte[size], _ => { });
        SetBudget(fixture.Session, budget);
        var held = budget.TryRent() ?? throw new Exception("test budget rent failed");
        try
        {
            await fixture.Pump(new ValuesStream(5, 6)).WaitAsync(Timeout);
            await fixture.Flush();
            Ensure(fixture.Transport.Snapshot().All(static write => write.PageArray is null),
                "budget exhaustion must select writer-backed fallback, not wait or bypass the limit");
            Ensure(fixture.Frames().Count == 3, "fallback must preserve both values and normal completion");
            Ensure(budget.BytesOwned == 4096, "fallback must not borrow another page reservation");
        }
        finally
        {
            held.Release();
        }
        Ensure(budget.BytesOwned == 0, "the unrelated physical owner must remain independently releasable");
    }

    [Test]
    public async Task CustomSizedCodecMustStayOnFallbackAndPreserveItsWireBytes()
    {
        await using var fixture = new Fixture();
        var codec = new CustomSizedIntCodec();
        await fixture.Pump(new ValuesStream(7, 8), codec).WaitAsync(Timeout);
        await fixture.Flush();
        Ensure(codec.SerializeCount == 2, "custom exact-size codecs must still execute their own serializer");
        Ensure(fixture.Transport.Snapshot().All(static write => write.PageArray is null),
            "IRpcSizedCodec alone must not opt an arbitrary custom codec into trusted page encoding");
        var frames = fixture.Frames();
        Ensure(frames.Count == 3 && BinaryPrimitives.ReadInt32LittleEndian(frames[0].Payload.AsSpan(sizeof(ushort))) == 1007,
            "fallback must preserve custom codec wire semantics, not replace it with the built-in primitive codec");
        Ensure(fixture.Session.EncodedStreamPageBytesOwned == 0, "ineligible streams must not retain page capacity");
    }

    [Test]
    public async Task NegotiatedCompressionMustUseFallbackEvenForSmallPrimitivePayloads()
    {
        await using var fixture = new Fixture(compression: true);
        await fixture.Pump(new ValuesStream(12, 13)).WaitAsync(Timeout);
        await fixture.Flush();
        Ensure(fixture.Transport.Snapshot().All(static write => write.PageArray is null),
            "a negotiated compression binding must disable pages even below the compression size threshold");
        Ensure(fixture.Frames().Count == 3, "compression-ineligible pages must retain normal fallback framing");
        Ensure(fixture.Session.EncodedStreamPageBytesOwned == 0, "compression fallback must not reserve pages");
    }

    [Test]
    public async Task SessionTerminalBeforeEofPublicationMustReleaseTheRetainedProducerPage()
    {
        await using var fixture = new Fixture();
        var stream = new ControlledStream();
        var pump = fixture.Pump(stream);
        await stream.SecondMoveEntered.Task.WaitAsync(Timeout);
        await fixture.Transport.DirectWriteEntered.Task.WaitAsync(Timeout);
        Ensure(fixture.Session.EncodedStreamPageBytesOwned > 0,
            "the producer must still own its append page while waiting for EOF");
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "test session terminal");
        fixture.Session.NotifyDisconnected(terminal);
        stream.Finish.TrySetResult(false);
        await ExpectSameFailure(pump, terminal);
        await fixture.Session.DisposeAsync();
        Ensure(fixture.Session.EncodedStreamPageBytesOwned == 0,
            "terminal publication rejection must release the rejected frame and the producer page");
        Ensure(fixture.Frames().Count == 1, "a session terminal must prevent a later success StreamComplete");
    }

    [Test]
    public async Task CreditCancellationBeforeFirstItemMustNotPublishOrLeakItsReservation()
    {
        await using var fixture = new Fixture(flowControlled: true);
        await fixture.Session.AcquireStreamSendCreditAsync(99, 0, 4, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var pump = fixture.Pump(new ValuesStream(9), cancellationToken: cancellation.Token);
        Ensure(!pump.IsCompleted, "the test must actually block on exhausted connection credit");
        Ensure(fixture.Session.EncodedStreamPageBytesOwned <= EncodedStreamPageBudget.PageBytes,
            "a credit-blocked producer may retain at most its bounded append-page reservation");
        cancellation.Cancel();
        await ExpectCancellation(pump);
        Ensure(fixture.Session.EncodedStreamPageBytesOwned == 0 && fixture.Transport.Snapshot().Length == 0,
            "credit cancellation must not publish bytes or retain page ownership");
    }

    [Test]
    public async Task QueueRejectionShouldConsumeTheTransferredFrameReferenceExactlyOnce()
    {
        await using var fixture = new Fixture(holdDirectWrite: true, maxQueueBytes: 1);
        var returned = 0;
        var budget = new EncodedStreamPageBudget(4096, size => new byte[size], _ => returned++);
        using var writer = new EncodedStreamPageWriter(budget);
        try
        {
            // The queue deliberately permits one oversized frame when empty. Hold that
            // first frame physically so the second publication encounters actual saturation.
            fixture.Session.PublishStreamPageFrame(CreateCompletionFrame());
            await fixture.Transport.DirectWriteEntered.Task.WaitAsync(Timeout);
            var rejected = CreateCompletionFrame();
            try
            {
                fixture.Session.PublishStreamPageFrame(rejected);
                throw new Exception("expected queue-capacity rejection");
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
            {
            }
            Ensure(returned == 0 && budget.BytesOwned == 4096,
                "rejection must consume its Q without consuming producer P or the first physical frame's Q");
            writer.Dispose();
            Ensure(returned == 0 && budget.BytesOwned == 4096,
                "producer disposal after rejection must leave the first physical frame's ownership intact");
            Ensure(fixture.Transport.Snapshot().Length == 1, "the rejected second frame must never reach transport");
        }
        finally
        {
            fixture.Transport.ReleaseDirectWrite.TrySetResult();
        }
        await fixture.Flush();
        Ensure(returned == 1 && budget.BytesOwned == 0,
            "the accepted physical frame must release the sole remaining reference exactly once");

        OwnedFrame CreateCompletionFrame()
        {
            Ensure(writer.TryBeginFrame(ProtocolV2Constants.HeaderBytes + sizeof(ushort)), "completion frame must fit");
            using (writer.BeginPacketScope(ProtocolV2FrameType.StreamComplete, ProtocolV2FrameFlags.None, 73))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(writer.GetSpan(sizeof(ushort)), 0);
                writer.Advance(sizeof(ushort));
            }
            return writer.CommitFrame();
        }
    }

    [Test]
    public async Task GeneratedQueueRejectionMustRefundOnlyTheUnpublishedItemAndDrainAllPages()
    {
        await using var fixture = new Fixture(holdDirectWrite: true, flowControlled: true,
            maxQueueBytes: 4096, windowBytes: 4096);
        var returned = 0;
        var budget = new EncodedStreamPageBudget(8192, size => new byte[size], _ => Interlocked.Increment(ref returned));
        SetBudget(fixture.Session, budget);
        var controller = GetFlowController(fixture.Session);
        var before = controller.SendConnectionCredit;
        try
        {
            try
            {
                await fixture.Pump(new ValuesStream(Enumerable.Range(0, 300).ToArray())).WaitAsync(Timeout);
                throw new Exception("expected queue-capacity rejection");
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
            {
            }
            await fixture.Transport.DirectWriteEntered.Task.WaitAsync(Timeout);
            Ensure(budget.BytesOwned == 4096 && returned == 1,
                "the rejected next-page slice and its producer must retire while the accepted first page remains physical");
        }
        finally
        {
            fixture.Transport.ReleaseDirectWrite.TrySetResult();
        }
        await fixture.Flush();
        var frames = fixture.Frames();
        Ensure(frames.Count > 0 && frames.All(static frame => frame.Type == ProtocolV2FrameType.StreamData),
            "queue rejection must preserve accepted data without publishing a success terminal");
        Ensure(controller.SendConnectionCredit == before - frames.Count * sizeof(int),
            "queue-rejected bytes must be refunded, but accepted frames must retain their consumed send credit");
        Ensure(budget.BytesOwned == 0 && returned == 2, "all accepted and rejected page references must drain exactly once");
    }

    private static void SetBudget(RpcSession session, EncodedStreamPageBudget budget)
        => (typeof(RpcSession).GetField("_encodedStreamPageBudget", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("encoded-page budget field missing")).SetValue(session, budget);

    private static StreamFlowController GetFlowController(RpcSession session)
    {
        var state = (RpcSessionProtocolState)(typeof(RpcSession).GetField("_protocolState",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session)
            ?? throw new Exception("protocol state field missing"));
        return state.FlowController ?? throw new Exception("flow control was not negotiated");
    }

    private static async Task ExpectSameFailure(Task task, Exception expected)
    {
        try { await task.WaitAsync(Timeout); }
        catch (Exception exception) when (ReferenceEquals(exception, expected)) { return; }
        throw new Exception("Generated stream did not preserve the enumeration failure.");
    }

    private static async Task ExpectCancellation(Task task)
    {
        try { await task.WaitAsync(Timeout); }
        catch (OperationCanceledException exception)
        {
            if (SharpLinkPhysicalWork.GetAbandonedUserWork(exception) is { } cleanup)
                await cleanup.WaitAsync(Timeout);
            return;
        }
        throw new Exception("Generated stream did not observe cancellation.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SharpLinkRuntimeContext _context;
        internal RecordingTransport Transport { get; }
        internal RpcSession Session { get; }

        internal Fixture(bool holdDirectWrite = false, bool flowControlled = false, int? maxQueueBytes = null,
            bool compression = false, int windowBytes = 4)
        {
            _context = new SharpLinkRuntimeContextBuilder()
                .Configure(options =>
                {
                    if (maxQueueBytes.HasValue) options.FlowControl.MaxSendQueueBytes = maxQueueBytes.Value;
                    if (compression) options.Compression.Providers.Add(new TestCompressionProvider());
                }).Build(includeGeneratedAssemblyCatalog: false);
            Transport = new RecordingTransport(holdDirectWrite);
            Session = new RpcSession(Transport, RpcSessionTestFixture.ServerOptions(_context,
                new RpcSessionFlushOptions(1, TimeSpan.FromMilliseconds(1))));
            RpcSessionTestFixture.CompleteHandshake(Session,
                (flowControlled ? ProtocolV2Capabilities.FlowControl : ProtocolV2Capabilities.None) |
                (compression ? ProtocolV2Capabilities.Compression : ProtocolV2Capabilities.None),
                streamReceiveWindowBytes: windowBytes, connectionReceiveWindowBytes: windowBytes,
                compressionBinding: compression ? _context.Compression.ProviderBindings[0] : null);
        }

        internal Task Pump(IAsyncEnumerable<int> stream, IRpcCodec<int>? codec = null,
            CancellationToken cancellationToken = default)
            => Session.PumpGeneratedOutboundStreamAsync(73, 0, stream,
                codec ?? Session.RuntimeContext.Codecs.GetCodec<int>(), false, cancellationToken).AsTask();

        internal Task Flush() => Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);

        internal List<ParsedFrame> Frames()
        {
            var bytes = Transport.Snapshot().SelectMany(static write => write.Bytes).ToArray();
            var remaining = new ReadOnlySequence<byte>(bytes);
            var frames = new List<ParsedFrame>();
            while (ProtocolV2FrameParser.TryReadFrame(ref remaining, Session.RuntimeContext.Protocol,
                out var header, out var payload))
            {
                Ensure(header.RequestId == 73, "page frames must preserve the request ID");
                Ensure(BinaryPrimitives.ReadUInt16LittleEndian(payload.FirstSpan) == 0,
                    "page frames must preserve the stream ID");
                frames.Add(new ParsedFrame(header.Type, header.Flags, payload.ToArray()));
            }
            Ensure(remaining.IsEmpty, "every published page slice must be one or more complete protocol frames");
            return frames;
        }

        public async ValueTask DisposeAsync()
        {
            Transport.ReleaseDirectWrite.TrySetResult();
            await Session.DisposeAsync();
            _context.Dispose();
        }
    }

    private sealed record ParsedFrame(ProtocolV2FrameType Type, ProtocolV2FrameFlags Flags, byte[] Payload);
    private sealed record RecordedWrite(byte[] Bytes, byte[]? PageArray);

    private sealed class RecordingTransport : ITransportConnection, IStreamPageTransport
    {
        private readonly List<RecordedWrite> _writes = new();
        private readonly Pipe _input = new();
        private readonly bool _holdDirectWrite;
        internal readonly TaskCompletionSource DirectWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseDirectWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ReadOnlyMemory<byte> HeldMemory { get; private set; }

        internal RecordingTransport(bool holdDirectWrite)
        {
            _holdDirectWrite = holdDirectWrite;
            Output = new RecordingWriter(bytes => Record(new RecordedWrite(bytes, null)));
        }

        public string Id => "generated-page-recording";
        public PipeReader Input => _input.Reader;
        public PipeWriter Output { get; }
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;

        public ValueTask WriteStreamPageAsync(ReadOnlyMemory<byte> memory, CancellationToken token)
        {
            Ensure(MemoryMarshal.TryGetArray(memory, out var segment), "page output must preserve its array-backed lease");
            Record(new RecordedWrite(memory.ToArray(), segment.Array));
            HeldMemory = memory;
            DirectWriteEntered.TrySetResult();
            return _holdDirectWrite ? new ValueTask(ReleaseDirectWrite.Task) : ValueTask.CompletedTask;
        }

        private void Record(RecordedWrite write) { lock (_writes) _writes.Add(write); }
        internal RecordedWrite[] Snapshot() { lock (_writes) return _writes.ToArray(); }
        public async ValueTask DisposeAsync()
        {
            await Output.CompleteAsync();
            await _input.Reader.CompleteAsync();
            await _input.Writer.CompleteAsync();
        }
    }

    private sealed class RecordingWriter(Action<byte[]> record) : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        public override void Advance(int bytes) => _buffer.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (_buffer.WrittenCount != 0)
            {
                record(_buffer.WrittenSpan.ToArray());
                _buffer.Clear();
            }
            return ValueTask.FromResult(new FlushResult(false, false));
        }
    }

    private sealed class ValuesStream(params int[] values) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _index = -1;
        public int Current => values[_index];
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(++_index < values.Length);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledStream(bool honorCancellation = true) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _moves;
        private CancellationToken _cancellation;
        internal readonly TaskCompletionSource SecondMoveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => 37;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { _cancellation = cancellationToken; return this; }
        public ValueTask<bool> MoveNextAsync()
        {
            if (_moves++ == 0) return ValueTask.FromResult(true);
            SecondMoveEntered.TrySetResult();
            return new ValueTask<bool>(honorCancellation ? Finish.Task.WaitAsync(_cancellation) : Finish.Task);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CustomSizedIntCodec : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        internal int SerializeCount { get; private set; }
        public bool CanExactSize => true;
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            SerializeCount++;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.GetSpan(sizeof(int)), value + 1000);
            buffer.Advance(sizeof(int));
        }
        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BinaryPrimitives.ReadInt32LittleEndian(buffer.FirstSpan) - 1000;
        public bool TryGetEncodedSize(in int value, out int size) { size = sizeof(int); return true; }
        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        { size = sizeof(int); snapshot = null; return true; }
        public void SerializeSized(in int value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)
            => Serialize(value, buffer);
        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot) { }
    }
}
