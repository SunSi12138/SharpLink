using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class SendPumpTests
{
    [Test]
    public async Task HugeFlushLatencyShouldNotOverflowIntoImmediateFlush()
    {
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "huge-flush-latency",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, TimeSpan.MaxValue)));
        var frame = CreateFrame(session, 32, requestId: 1);

        session.SendPacket(frame);
        await Task.Delay(100);
        var connectedBeforeCleanup = session.IsConnected;
        var queuedBeforeCleanup = session.QueuedSendBytes;

        await session.DisposeAsync();
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
        Ensure(connectedBeforeCleanup,
            "a huge configured batching latency must not fault the send pump");
        Ensure(queuedBeforeCleanup > 0,
            "a huge configured batching latency must not overflow into an immediate flush");
        EnsureReturned(frame, "dispose should return the long-batched frame owner");
    }

    [Test]
    public async Task TimedBatchShouldFlushAtExactProviderLatencyAndReturnItsOwner()
    {
        var clock = new ManualTimeProvider();
        var maxLatency = TimeSpan.FromSeconds(5);
        var provider = new TimerArmObservingTimeProvider(clock, maxLatency);
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "timed-batch-provider-boundary",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, maxLatency)));
        var frame = CreateFrame(session, 32, requestId: 1);
        try
        {
            var flushedBytes = output.Reader.ReadAsync().AsTask();
            session.SendPacket(frame);
            await provider.ExpectedTimerArmed;
            Ensure(clock.EarliestTimerTimestamp == maxLatency.Ticks,
                "the first small frame must arm its exact provider MaxLatency boundary");

            clock.Advance(maxLatency.Subtract(TimeSpan.FromTicks(1)));
            Ensure(!flushedBytes.IsCompleted && session.QueuedSendBytes > 0,
                "the small frame must remain batched one provider tick before MaxLatency");

            clock.Advance(TimeSpan.FromTicks(1));
            var read = await flushedBytes;
            Ensure(read.Buffer.Length > ProtocolV2Constants.HeaderBytes,
                "exact provider equality must make the timed batch visible to the transport");
            output.Reader.AdvanceTo(read.Buffer.End);

            await session.FlushSendQueueAsync();
            await clock.WaitForTimersDrainedAsync();
            EnsureReturned(frame,
                "the equality flush must return the small-frame owner before its queue barrier completes");
            Ensure(session.QueuedSendBytes == 0 && clock.ActiveTimerCount == 0,
                "the equality flush must release queued bytes and its provider timer");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task TimedBatchShouldDeliverFrameSentAfterDeadlineFlushThroughRetainedRead()
    {
        var clock = new ManualTimeProvider();
        var maxLatency = TimeSpan.FromMilliseconds(100);
        var provider = new TimerArmRecordingTimeProvider(clock);
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "timed-batch-retained-read",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, maxLatency)));
        try
        {
            var first = CreateFrame(session, 32, requestId: 1);
            session.SendPacket(first);
            await WaitUntilAsync(() => provider.WasArmed(maxLatency));
            clock.Advance(maxLatency);

            await ConsumeAvailableAsync(output.Reader);
            await WaitUntilAsync(() => session.QueuedSendBytes == 0);
            EnsureReturned(first, "the deadline flush must return the first frame owner");
            Ensure(clock.ActiveTimerCount == 0,
                "the deadline timer must be disposed after the timed-out flush");

            // The pump must have kept the unconsumed pending read and re-observed it: the next
            // frame wakes the pump through that retained registration. Dropping the read would
            // leave the fresh registration waiting behind the stale one and time this out.
            var second = CreateFrame(session, 32, requestId: 2);
            await session.SendPacketAndFlushAsync(second).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            EnsureReturned(second,
                "a frame after a deadline flush must be delivered through the retained pending read");
            await ConsumeAvailableAsync(output.Reader);
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task TimedBatchShouldExtendBatchForFrameArrivingBeforeDeadline()
    {
        var clock = new ManualTimeProvider();
        var maxLatency = TimeSpan.FromMilliseconds(100);
        var provider = new TimerArmRecordingTimeProvider(clock);
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "timed-batch-extension",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, maxLatency)));
        var first = CreateFrame(session, 32, requestId: 1);
        var second = CreateFrame(session, 32, requestId: 2);
        try
        {
            session.SendPacket(first);
            await WaitUntilAsync(() => provider.WasArmed(maxLatency));
            clock.Advance(TimeSpan.FromMilliseconds(50));

            session.SendPacket(second);
            // The arriving frame wins the deadline race. The pump then re-arms one timer for
            // the remaining latency, which is the durable observation point (the transient
            // dispose-then-rearm handoff is too short to poll for).
            await WaitUntilAsync(() => provider.WasArmed(TimeSpan.FromMilliseconds(50)));

            clock.Advance(TimeSpan.FromMilliseconds(50));
            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var expectedBytes = 2 * (ProtocolV2Constants.HeaderBytes + 32);
            Ensure(read.Buffer.Length >= expectedBytes,
                "both frames must share one flush at the first frame's deadline");
            output.Reader.AdvanceTo(read.Buffer.End);

            await WaitUntilAsync(() => session.QueuedSendBytes == 0);
            EnsureReturned(first, "the extended batch must return the first frame owner");
            EnsureReturned(second, "the extended batch must return the second frame owner");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task TimedBatchShouldIgnoreStaleTimerCallbackFromPreviousArm()
    {
        var clock = new ManualTimeProvider();
        var maxLatency = TimeSpan.FromMilliseconds(100);
        var provider = new StaleCallbackTimeProvider(clock);
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "timed-batch-stale-timer-callback",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, maxLatency)));
        var first = CreateFrame(session, 32, requestId: 1);
        var second = CreateFrame(session, 32, requestId: 2);
        try
        {
            session.SendPacket(first);
            await WaitUntilAsync(() => provider.WasArmed(maxLatency));
            clock.Advance(TimeSpan.FromMilliseconds(50));

            session.SendPacket(second);
            // The arriving frame wins the first race; the pump re-arms for the remaining
            // latency, which is the durable observation that the first arm is superseded.
            await WaitUntilAsync(() => provider.WasArmed(TimeSpan.FromMilliseconds(50)));

            // The first arm's timer callback fires out of band, as if it had been dequeued by
            // the timer queue but not yet executed. It belongs to a superseded arm and must not
            // disarm the current deadline timer or complete the current wait.
            provider.InvokeArmedCallback(0);
            await Task.Delay(50);
            Ensure(clock.ActiveTimerCount == 1,
                "a stale timer callback must not disarm the current deadline timer");

            clock.Advance(TimeSpan.FromMilliseconds(50));
            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var expectedBytes = 2 * (ProtocolV2Constants.HeaderBytes + 32);
            Ensure(read.Buffer.Length >= expectedBytes,
                "both frames must share one flush at the first frame's deadline");
            output.Reader.AdvanceTo(read.Buffer.End);

            await WaitUntilAsync(() => session.QueuedSendBytes == 0);
            EnsureReturned(first, "the deadline flush must return the first frame owner");
            EnsureReturned(second, "the deadline flush must return the second frame owner");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task TimedBatchShouldRearmAcrossMaximumTimerDelayChunks()
    {
        var clock = new ManualTimeProvider();
        var chunk = TimeSpan.FromMilliseconds(int.MaxValue);
        var maxLatency = chunk + TimeSpan.FromMilliseconds(1);
        var provider = new TimerArmRecordingTimeProvider(clock);
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "timed-batch-chunk-rearm",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, maxLatency)));
        var frame = CreateFrame(session, 32, requestId: 1);
        try
        {
            session.SendPacket(frame);
            await WaitUntilAsync(() => provider.WasArmed(chunk));
            Ensure(clock.EarliestTimerTimestamp == clock.GetTimestamp() + chunk.Ticks,
                "a deadline beyond the maximum timer delay must be armed as one full chunk");

            clock.Advance(chunk);
            Ensure(session.QueuedSendBytes > 0,
                "an expiring timer-delay chunk must not flush a batch whose deadline is still ahead");
            await WaitUntilAsync(() =>
                clock.EarliestTimerTimestamp == clock.GetTimestamp() + TimeSpan.FromMilliseconds(1).Ticks);

            clock.Advance(TimeSpan.FromMilliseconds(1));
            await ConsumeAvailableAsync(output.Reader);
            await WaitUntilAsync(() => session.QueuedSendBytes == 0);
            EnsureReturned(frame, "the re-armed deadline flush must return the frame owner");
            Ensure(clock.ActiveTimerCount == 0, "the final deadline timer must be disposed");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task TimedBatchDeadlineWaitShouldExitWhenSessionIsDisposed()
    {
        var clock = new ManualTimeProvider();
        var maxLatency = TimeSpan.FromMilliseconds(100);
        var provider = new TimerArmRecordingTimeProvider(clock);
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "timed-batch-dispose-during-wait",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, maxLatency)));
        var frame = CreateFrame(session, 32, requestId: 1);
        try
        {
            session.SendPacket(frame);
            await WaitUntilAsync(() => provider.WasArmed(maxLatency));

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            EnsureReturned(frame, "dispose during the deadline wait must return the frame owner");
            Ensure(session.QueuedSendBytes == 0, "dispose during the deadline wait must release queued bytes");
            Ensure(clock.ActiveTimerCount == 0,
                "the deadline timer must be disposed when the pump stops mid-wait");
        }
        finally
        {
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task FullByteQueueShouldFailFastWithoutClosingHealthySession()
    {
        var input = new Pipe();
        var output = CreateBackpressuredPipe();
        var session = CreateSession(input, output, maxSendQueueBytes: 1024);
        try
        {
            var first = CreateFrame(session, 800, requestId: 1);
            session.SendPacket(first);
            await WaitUntilAsync(() => session.QueuedSendBytes >= 800);

            var rejected = CreateFrame(session, 800, requestId: 2);
            var exception = CaptureSharpLinkException(() => session.SendPacket(rejected));
            Ensure(exception.Code == SharpLinkErrorCode.ResourceExhausted, "queue-full error code");
            EnsureReturned(rejected, "rejected owner should be returned");
            Ensure(session.IsConnected, "queue exhaustion must not close a healthy session");

            await ConsumeAvailableAsync(output.Reader);
            await WaitUntilAsync(() => session.QueuedSendBytes == 0);
            EnsureReturned(first, "flushed owner should be returned");

            var afterRecovery = CreateFrame(session, 32, requestId: 3);
            session.SendPacket(afterRecovery);
            await ConsumeAvailableAsync(output.Reader);
            await WaitUntilAsync(() => session.QueuedSendBytes == 0);
            EnsureReturned(afterRecovery, "recovered frame owner should be returned");
            Ensure(session.IsConnected, "session should accept frames after capacity recovers");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task WaitingAdmissionShouldResumeAfterFlushedBytesAreReleased()
    {
        var input = new Pipe();
        var output = CreateBackpressuredPipe();
        var session = CreateSession(input, output, maxSendQueueBytes: 1024);
        try
        {
            session.SendPacket(CreateFrame(session, 800, requestId: 1));
            await WaitUntilAsync(() => session.QueuedSendBytes >= 800);

            var waitingFrame = CreateFrame(session, 800, requestId: 2);
            var admission = session.SendPacketAsync(
                waitingFrame,
                waitForCapacity: true,
                forceFlush: false).AsTask();
            await Task.Delay(50);
            Ensure(!admission.IsCompleted, "admission should wait while byte capacity is exhausted");

            await ConsumeAvailableAsync(output.Reader);
            await admission.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(session.QueuedSendBytes > 0, "accepted frame should remain owned until its flush completes");

            await ConsumeAvailableAsync(output.Reader);
            await WaitUntilAsync(() => session.QueuedSendBytes == 0);
            EnsureReturned(waitingFrame, "waiting frame owner should be returned after flush");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task CancelledAdmissionShouldReturnUnacceptedOwner()
    {
        var input = new Pipe();
        var output = CreateBackpressuredPipe();
        var session = CreateSession(input, output, maxSendQueueBytes: 1024);
        try
        {
            session.SendPacket(CreateFrame(session, 800, requestId: 1));
            await WaitUntilAsync(() => session.QueuedSendBytes >= 800);

            var cancelledFrame = CreateFrame(session, 800, requestId: 2);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            try
            {
                await session.SendPacketAsync(
                    cancelledFrame,
                    waitForCapacity: true,
                    forceFlush: false,
                    cancellation.Token);
                throw new Exception("expected admission cancellation");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            EnsureReturned(cancelledFrame, "cancelled admission should return its owner");
            Ensure(session.IsConnected, "admission cancellation must not close the session");
            await ConsumeAvailableAsync(output.Reader);
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task ForceFlushMarkerShouldStillUsePumpAndReturnAfterFlush()
    {
        var input = new Pipe();
        var output = new Pipe();
        var context = new SharpLinkRuntimeContextBuilder()
            .Configure(static options => options.PerformanceProfile = SharpLinkPerformanceProfile.Throughput)
            .Build();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "force-flush",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        try
        {
            var frame = CreateFrame(session, 32, requestId: 1);
            await session.SendPacketAndFlushAsync(frame).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            EnsureReturned(frame, "force-flushed owner should be returned before completion");
            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(read.Buffer.Length > ProtocolV2Constants.HeaderBytes, "force-flushed bytes should be visible");
            output.Reader.AdvanceTo(read.Buffer.End);
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    private static RpcSession CreateSession(Pipe input, Pipe output, int maxSendQueueBytes)
    {
        var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.FlowControl.MaxSendQueueBytes = maxSendQueueBytes)
            .Build();
        return RpcSessionTestFixture.CreateSessionOverTestTransport(
            "send-pump",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
    }

    private static Pipe CreateBackpressuredPipe()
        => new(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 0));

    private static IRpcByteBufferWriter CreateFrame(RpcSession session, int payloadBytes, ulong requestId)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
        using (writer.BeginPacketScope(
                   ProtocolV2FrameType.Response,
                   ProtocolV2FrameFlags.None,
                   requestId))
        {
            writer.Write(new byte[payloadBytes]);
        }
        return writer;
    }

    private static void EnsureReturned(IRpcByteBufferWriter writer, string message)
    {
        try
        {
            _ = writer.WrittenCount;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        throw new Exception(message);
    }

    private static SharpLinkException CaptureSharpLinkException(Action action)
    {
        try
        {
            action();
            throw new Exception("expected SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static async Task ConsumeAvailableAsync(PipeReader reader)
    {
        var read = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!read.Buffer.IsEmpty, "expected outbound bytes");
        reader.AdvanceTo(read.Buffer.End);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("condition was not reached");
            await Task.Delay(5);
        }
    }

    private sealed class TimerArmObservingTimeProvider(
        ManualTimeProvider inner,
        TimeSpan expectedDueTime) : TimeProvider
    {
        private readonly TaskCompletionSource _expectedTimerArmed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task ExpectedTimerArmed => _expectedTimerArmed.Task;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            return new HookedTimer(timer, changedDueTime =>
            {
                if (changedDueTime == expectedDueTime)
                    _expectedTimerArmed.TrySetResult();
            });
        }
    }

    private sealed class TimerArmRecordingTimeProvider(ManualTimeProvider inner) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<TimeSpan> _armedDueTimes = [];

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            return new HookedTimer(timer, changedDueTime =>
            {
                // The deadline race creates its timers disabled and arms them via Change, so
                // the arm is only observable on the Change hook, after the timer is installed
                // relative to the current clock position.
                lock (_gate)
                    _armedDueTimes.Add(changedDueTime);
            });
        }

        internal bool WasArmed(TimeSpan dueTime)
        {
            lock (_gate)
                return _armedDueTimes.Contains(dueTime);
        }
    }

    /// <summary>
    /// Wraps <see cref="ManualTimeProvider"/> and keeps every armed timer callback invocable
    /// out of band, simulating a fired timer whose callback is still queued after a later arm
    /// replaced the race state (timer-queue disposal cannot cancel an already dequeued work
    /// item).
    /// </summary>
    private sealed class StaleCallbackTimeProvider(ManualTimeProvider inner) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<(TimerCallback Callback, object? State)> _armedCallbacks = [];
        private readonly List<TimeSpan> _armedDueTimes = [];

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            lock (_gate)
                _armedCallbacks.Add((callback, state));
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            return new HookedTimer(timer, changedDueTime =>
            {
                lock (_gate)
                    _armedDueTimes.Add(changedDueTime);
            });
        }

        internal bool WasArmed(TimeSpan dueTime)
        {
            lock (_gate)
                return _armedDueTimes.Contains(dueTime);
        }

        internal void InvokeArmedCallback(int index)
        {
            TimerCallback callback;
            object? state;
            lock (_gate)
                (callback, state) = _armedCallbacks[index];
            callback(state);
        }
    }

    private sealed class HookedTimer(
        ITimer inner,
        Action<TimeSpan> onChangedDueTime) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var changed = inner.Change(dueTime, period);
            // Publish the observation only after the timer is actually armed: the tests
            // advance the manual clock once the arm is observed, and the due time must be
            // relative to the clock position at arm time.
            if (changed)
                onChangedDueTime(dueTime);
            return changed;
        }

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
