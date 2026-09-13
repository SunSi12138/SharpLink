using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;

namespace SharpLink.UnitTests.Runtime;

public class SendPumpTimeBudgetPublicationTests
{
    [Test]
    public async Task TimeBudgetShouldIncludeOutputSpanAcquisitionDelay()
    {
        var clock = new ManualTimeProvider();
        var input = new Pipe();
        var output = new Pipe();
        var advancingWriter = new AdvancingPipeWriter(output.Writer, clock);
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "time-budget-output-span-delay",
            input.Reader,
            advancingWriter,
            RpcSessionTestFixture.ClientOptions(context));
        var frame = CreateTimedRequestFrame();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), clock);

        try
        {
            advancingWriter.AdvanceClockOnNextBufferRequest(TimeSpan.FromSeconds(3));
            session.SendPacket(frame, deadline);

            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var bytes = read.Buffer.ToArray();
            output.Reader.AdvanceTo(read.Buffer.End);
            var budget = BinaryPrimitives.ReadInt64LittleEndian(
                bytes.AsSpan(
                    ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes,
                    sizeof(long)));

            Ensure(budget == TimeSpan.FromSeconds(7).Ticks,
                "the wire budget must include local PipeWriter span acquisition/copy delay");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task TimedRequestShouldPublishWithoutWaitingForLaterBatchWork()
    {
        var clock = new ManualTimeProvider();
        var maxLatency = TimeSpan.FromSeconds(30);
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "time-budget-publication-boundary",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, maxLatency)));
        var frame = CreateTimedRequestFrame();
        var deadline = RpcDeadline.Create(TimeSpan.FromMinutes(1), clock);

        try
        {
            session.SendPacket(frame, deadline);

            // Do not advance the fake clock. A ready deadline-bearing batch publishes
            // immediately instead of waiting for future work or the batching timer.
            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var bytes = read.Buffer.ToArray();
            output.Reader.AdvanceTo(read.Buffer.End);
            var budget = BinaryPrimitives.ReadInt64LittleEndian(
                bytes.AsSpan(
                    ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes,
                    sizeof(long)));

            Ensure(budget == TimeSpan.FromMinutes(1).Ticks,
                "a timed Request published without local delay must retain its full remaining budget");
            Ensure(clock.ActiveTimerCount == 0,
                "a timed Request publication boundary must not wait on the configured batch-latency timer");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task QueuedTimedRequestsShouldSharePublicationAndDropExpiredFrames(int expiredPosition)
    {
        var clock = new ManualTimeProvider();
        var input = new Pipe();
        using var writer = new BatchRecordingPipeWriter(clock);
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "timed-request-batch", input.Reader, writer,
            RpcSessionTestFixture.ClientOptions(
                context, new RpcSessionFlushOptions(1024 * 1024, TimeSpan.FromSeconds(30))));
        var observer = new RecordingFailureObserver(clock);
        var owners = new List<PooledByteBufferWriter>();
        try
        {
            var prefix = CreateRequestFrame(0, timed: false);
            owners.Add(prefix);
            session.SendPacket(prefix);
            await writer.FirstBufferRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var requestId = 1; requestId <= 3; requestId++)
            {
                var expires = expiredPosition == requestId || expiredPosition == 4;
                var owner = CreateRequestFrame(requestId, timed: true);
                owners.Add(owner);
                session.SendPacket(
                    owner, RpcDeadline.Create(TimeSpan.FromSeconds(expires ? 3 : 10), clock), observer);
                if (requestId == 2)
                {
                    var untimed = CreateRequestFrame(99, timed: false);
                    owners.Add(untimed);
                    session.SendPacket(untimed);
                }
            }
            writer.ReleaseFirstBuffer.Set();
            var publication = await writer.FirstPublication.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.FlushSendQueueAsync();

            var expectedIds = new long[] { 1, 2, 99, 3 }
                .Where(id => id == 99 || (expiredPosition != id && expiredPosition != 4)).ToArray();
            var actualIds = new List<long>();
            var bytes = publication.Bytes;
            var offset = ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes;
            while (offset < bytes.Length)
            {
                var requestId = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + 7, sizeof(long)));
                actualIds.Add(requestId);
                var timed = (((ProtocolV2FrameFlags)bytes[offset + 6]) & ProtocolV2FrameFlags.HasTimeBudget) != 0;
                Ensure(timed == (requestId != 99), "compaction must preserve each frame's time-budget flag");
                if (timed)
                {
                    var budget = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(
                        offset + ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes, sizeof(long)));
                    Ensure(budget == TimeSpan.FromSeconds(7).Ticks,
                        "every surviving request must deduct all batch-copy delay from its wire budget");
                }
                offset += ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes +
                    (timed ? sizeof(long) : 0);
            }
            Ensure(actualIds.SequenceEqual(expectedIds),
                "one publication must contain every surviving queued request in order and no expired request");
            Ensure(offset == bytes.Length, "compaction must preserve exact frame boundaries");
            Ensure(publication.Timestamp == TimeSpan.FromSeconds(3).Ticks,
                "expired-request callbacks must run after the surviving batch starts publication");
            var expectedExpired = Enumerable.Range(1, 3)
                .Where(id => expiredPosition == id || expiredPosition == 4).Select(id => (long)id);
            Ensure(observer.RequestIds.Order().SequenceEqual(expectedExpired),
                "every dropped request must complete its original owner exactly once without timer callbacks");
            foreach (var owner in owners)
            {
                var returned = false;
                try { _ = owner.WrittenCount; }
                catch (ObjectDisposedException) { returned = true; }
                Ensure(returned, "every standalone frame writer must be disposed after publication or expiry");
            }
            Ensure(session.QueuedSendBytes == 0,
                "publication and expiry must return every frame owner and reservation");
            Ensure(clock.ActiveTimerCount == 0, "a ready timed batch must not wait for the batching timer");
        }
        finally
        {
            writer.ReleaseFirstBuffer.Set();
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
        }
    }

    private sealed class RecordingFailureObserver(ManualTimeProvider clock) : IRequestEmissionFailureObserver
    {
        internal List<long> RequestIds { get; } = [];
        public void OnRequestEmissionFailure(long requestId, Exception exception)
        {
            Ensure(exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "only deadline expiry may remove a live transport's queued request");
            RequestIds.Add(requestId);
            clock.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(100));
        }
    }

    private sealed class BatchRecordingPipeWriter(ManualTimeProvider clock) : PipeWriter, IDisposable
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private int _bufferRequests;
        internal ManualResetEventSlim ReleaseFirstBuffer { get; } = new();
        internal TaskCompletionSource FirstBufferRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<(byte[] Bytes, long Timestamp)> FirstPublication { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Advance(int bytes) => _buffer.Advance(bytes);
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            FirstPublication.TrySetResult((_buffer.WrittenSpan.ToArray(), clock.GetTimestamp()));
            _buffer.Clear();
            return new(new FlushResult(isCanceled: false, isCompleted: false));
        }
        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            BeforeBufferRequest();
            return _buffer.GetMemory(sizeHint);
        }
        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            BeforeBufferRequest();
            return _buffer.GetSpan(sizeHint);
        }
        private void BeforeBufferRequest()
        {
            var request = ++_bufferRequests;
            if (request == 1)
            {
                FirstBufferRequested.TrySetResult();
                if (!ReleaseFirstBuffer.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("test did not release the initial output buffer");
            }
            else if (request == 2)
                clock.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(3));
        }
        public void Dispose() => ReleaseFirstBuffer.Dispose();
    }

    private static PooledByteBufferWriter CreateTimedRequestFrame()
        => CreateRequestFrame(1, timed: true);

    private static PooledByteBufferWriter CreateRequestFrame(long requestId, bool timed)
    {
        var frame = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(
            frame,
            ProtocolV2FrameType.Request,
            timed ? ProtocolV2FrameFlags.HasTimeBudget : ProtocolV2FrameFlags.None,
            unchecked((ulong)requestId));
        frame.Advance(ProtocolV2Constants.RequestPrefixBytes);
        if (timed)
            frame.Advance(sizeof(long));
        ProtocolV2FrameWriter.EndFrame(frame, token);
        return frame;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class AdvancingPipeWriter(
        PipeWriter inner,
        ManualTimeProvider clock) : PipeWriter
    {
        private long _advanceTicks;
        private int _armed;

        internal void AdvanceClockOnNextBufferRequest(TimeSpan delay)
        {
            _advanceTicks = delay.Ticks;
            Volatile.Write(ref _armed, 1);
        }

        public override void Advance(int bytes) => inner.Advance(bytes);

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null)
            => inner.CompleteAsync(exception);

        public override ValueTask<FlushResult> FlushAsync(
            CancellationToken cancellationToken = default)
            => inner.FlushAsync(cancellationToken);

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            AdvanceClockIfArmed();
            return inner.GetMemory(sizeHint);
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            AdvanceClockIfArmed();
            return inner.GetSpan(sizeHint);
        }

        private void AdvanceClockIfArmed()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
                return;
            clock.AdvanceWithoutRunningTimers(TimeSpan.FromTicks(_advanceTicks));
        }
    }
}
