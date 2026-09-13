using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// The request's wire TimeBudget is produced exactly once by the caller that builds the frame.
/// The send pump copies frames verbatim: it does not re-sample, re-stamp, compact, or locally
/// expire deadline-bearing requests. End-to-end expiry is enforced by the caller's pending
/// deadline and by the remote cancellation path, not by the local send queue.
/// </summary>
public class SendPumpTimeBudgetPublicationTests
{
    [Test]
    public async Task TimedRequestShouldReachTheTransportVerbatim()
    {
        var clock = new ManualTimeProvider();
        var input = new Pipe();
        var output = new Pipe();
        var advancingWriter = new AdvancingPipeWriter(output.Writer, clock);
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "time-budget-verbatim",
            input.Reader,
            advancingWriter,
            RpcSessionTestFixture.ClientOptions(context));
        var budget = TimeSpan.FromSeconds(7);
        var frame = CreateTimedRequestFrame(budget);

        try
        {
            // Local buffer acquisition and copy still consume wall-clock time. That delay is no
            // longer folded into the wire budget: the producer stamped it before the pump ran.
            advancingWriter.AdvanceClockOnNextBufferRequest(TimeSpan.FromSeconds(3));
            session.SendPacket(frame);

            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var bytes = read.Buffer.ToArray();
            output.Reader.AdvanceTo(read.Buffer.End);

            Ensure(ReadPublishedBudget(bytes) == budget.Ticks,
                "the pump must publish the producer-stamped wire budget unchanged");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task TimedRequestShouldFollowTheConfiguredBatchLatency()
    {
        var clock = new ManualTimeProvider();
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "time-budget-batch-boundary",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, TimeSpan.FromSeconds(30))));
        var budget = TimeSpan.FromMinutes(1);
        var frame = CreateTimedRequestFrame(budget);

        try
        {
            session.SendPacket(frame);

            // A deadline no longer bypasses the configured batch latency: the pump treats every
            // frame the same and publishes at the batch boundary. The budget the caller stamped
            // is what the peer sees, so the wait is deducted end to end by the peer's own clock.
            var pendingRead = output.Reader.ReadAsync().AsTask();
            var first = await Task.WhenAny(pendingRead, Task.Delay(TimeSpan.FromMilliseconds(250)));
            Ensure(!ReferenceEquals(first, pendingRead),
                "a quiet queue must hold the batch until the configured latency boundary");

            clock.Advance(TimeSpan.FromSeconds(30));
            var read = await pendingRead.WaitAsync(TimeSpan.FromSeconds(2));
            var bytes = read.Buffer.ToArray();
            output.Reader.AdvanceTo(read.Buffer.End);
            Ensure(ReadPublishedBudget(bytes) == budget.Ticks,
                "the batch boundary must not rewrite the caller's wire budget");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task ExpiredTimedRequestsShouldStillBePublishedInOrder()
    {
        var clock = new ManualTimeProvider();
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "time-budget-no-local-expiry",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        var observer = new RecordingFailureObserver();
        var owners = new List<PooledByteBufferWriter>();
        var expiredBudget = TimeSpan.Zero;
        var freshBudget = TimeSpan.FromSeconds(10);

        try
        {
            // The first request is already past its deadline when it enters the queue. The local
            // pump must not drop it: the caller's pending deadline (or the peer) owns that outcome.
            owners.Add(CreateRequestFrame(1, expiredBudget));
            owners.Add(CreateRequestFrame(2, null));
            owners.Add(CreateRequestFrame(3, freshBudget));
            session.SendPacket(owners[0], observer);
            session.SendPacket(owners[1]);
            session.SendPacket(owners[2]);

            var bytes = await ReadAllAsync(output.Reader).WaitAsync(TimeSpan.FromSeconds(2));
            var requestIds = new List<long>();
            var offset = 0;
            var bounds = new List<long?>();
            while (offset < bytes.Length)
            {
                var requestId = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + 7, sizeof(long)));
                var timed = (((ProtocolV2FrameFlags)bytes[offset + 6]) & ProtocolV2FrameFlags.HasTimeBudget) != 0;
                requestIds.Add(requestId);
                bounds.Add(timed
                    ? BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(
                        offset + ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes,
                        sizeof(long)))
                    : null);
                offset += ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes +
                    (timed ? sizeof(long) : 0);
            }

            Ensure(requestIds.SequenceEqual([1L, 2L, 3L]),
                "every queued request must be published in order, including an already-expired one");
            Ensure(bounds[0] == expiredBudget.Ticks && bounds[1] is null && bounds[2] == freshBudget.Ticks,
                "the pump must publish each caller-stamped budget unchanged");
            Ensure(offset == bytes.Length, "publication must preserve exact frame boundaries");
            Ensure(observer.RequestIds.Count == 0,
                "the local pump must not report emission-time deadline failures");
            Ensure(session.QueuedSendBytes == 0,
                "publication must return every frame owner and reservation");
        }
        finally
        {
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
        }
    }

    private static async Task<byte[]> ReadAllAsync(PipeReader reader)
    {
        var buffer = new MemoryStream();
        while (buffer.Length == 0)
        {
            var read = await reader.ReadAsync().AsTask();
            buffer.Write(read.Buffer.ToArray());
            reader.AdvanceTo(read.Buffer.End);
        }
        return buffer.ToArray();
    }

    private static long ReadPublishedBudget(byte[] bytes)
        => BinaryPrimitives.ReadInt64LittleEndian(
            bytes.AsSpan(
                ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes,
                sizeof(long)));

    private sealed class RecordingFailureObserver : IRequestEmissionFailureObserver
    {
        internal List<long> RequestIds { get; } = [];
        public void OnRequestEmissionFailure(long requestId, Exception exception) => RequestIds.Add(requestId);
    }

    private static PooledByteBufferWriter CreateTimedRequestFrame(TimeSpan budget)
        => CreateRequestFrame(1, budget);

    private static PooledByteBufferWriter CreateRequestFrame(long requestId, TimeSpan? budget)
    {
        var frame = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(
            frame,
            ProtocolV2FrameType.Request,
            budget.HasValue ? ProtocolV2FrameFlags.HasTimeBudget : ProtocolV2FrameFlags.None,
            unchecked((ulong)requestId));
        frame.Advance(ProtocolV2Constants.RequestPrefixBytes);
        if (budget.HasValue)
        {
            var budgetSpan = frame.GetSpan(sizeof(long));
            BinaryPrimitives.WriteInt64LittleEndian(budgetSpan, budget.Value.Ticks);
            frame.Advance(sizeof(long));
        }
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
