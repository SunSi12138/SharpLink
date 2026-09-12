using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

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

            // Do not advance the fake clock. A deadline-bearing Request closes the current
            // batch and publishes immediately, so later batching work cannot happen after its
            // remaining TimeBudget has already been sampled.
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

    private static PooledByteBufferWriter CreateTimedRequestFrame()
    {
        var frame = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(
            frame,
            ProtocolV2FrameType.Request,
            ProtocolV2FrameFlags.HasTimeBudget,
            1);
        frame.Advance(ProtocolV2Constants.RequestPrefixBytes);
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
