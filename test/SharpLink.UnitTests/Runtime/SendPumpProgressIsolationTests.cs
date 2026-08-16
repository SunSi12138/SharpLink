using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Issue #163: protocol-progress isolation in the session send pump.
/// Progress frames (ping/pong, cancel, window update, go-away) are admitted
/// against a small reserved headroom and drained in a bounded priority burst,
/// while normal frames keep strict FIFO order among themselves.
/// </summary>
public class SendPumpProgressIsolationTests
{
    [Test]
    public async Task ProgressFrameOvertakesEarlierQueuedBulkFrames()
    {
        var clock = new ManualTimeProvider();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "progress-overtakes-bulk",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, TimeSpan.FromSeconds(10))));
        try
        {
            // Park the pump in the timed-batch deadline wait, then queue bulk
            // frames followed by a progress frame while the pump is blocked.
            session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Response, 128, requestId: 1));
            await WaitUntilAsync(() => clock.EarliestTimerTimestamp != long.MaxValue);

            // Enqueue the progress frame first so the pump's wake-up race sees
            // it regardless of which read claims first, then the bulk frames.
            session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Ping, 8, requestId: 0));
            for (var index = 0; index < 32; index++)
            {
                session.SendPacket(CreateFrame(
                    session, ProtocolV2FrameType.Response, 256, checked((ulong)index + 2)));
            }
            // The pump drains the burst and the bulk frames, then parks the
            // remaining batch in a fresh deadline wait; the reader advances the
            // manual clock whenever the pipe has no data so every parked batch
            // flushes deterministically.
            var types = await ReadFrameTypesWithClockAsync(
                output.Reader, context.Protocol, clock, expectedFrames: 34);
            Ensure(types.Count == 34, $"expected 34 frames, read {types.Count}");
            Ensure(types[0] == ProtocolV2FrameType.Response,
                "the frame already staged before the deadline wait keeps its position");
            Ensure(types[1] == ProtocolV2FrameType.Ping,
                "a progress frame must be flushed before bulk frames that were queued earlier");
            Ensure(types.Skip(2).All(static type => type == ProtocolV2FrameType.Response),
                "the bulk frames behind the progress frame must keep their class");
        }
        finally
        {
            await session.DisposeAsync();
            await clock.WaitForTimersDrainedAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task CancelNeverOvertakesItsQueuedRequest()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "cancel-request-order",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        try
        {
            // A cancel must stay in the normal class: the peer discards
            // cancels for requests it has not dispatched yet, so a cancel
            // overtaking its own request would let the request execute after
            // the caller already cancelled it.
            session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Request, 64, requestId: 7));
            session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Cancel, 0, requestId: 7));

            var types = await ReadFrameTypesAsync(output.Reader, context.Protocol, expectedFrames: 2);
            Ensure(types.Count == 2, $"expected 2 frames, read {types.Count}");
            Ensure(types[0] == ProtocolV2FrameType.Request,
                "the request must reach the transport before its cancel");
            Ensure(types[1] == ProtocolV2FrameType.Cancel,
                "the cancel must follow the request it cancels");
        }
        finally
        {
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task StreamCompleteNeverOvertakesQueuedStreamData()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "stream-complete-order",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        try
        {
            const ulong requestId = 42;
            for (var index = 0; index < 3; index++)
                session.SendPacket(CreateFrame(session, ProtocolV2FrameType.StreamData, 512, requestId));
            session.SendPacket(CreateFrame(session, ProtocolV2FrameType.StreamComplete, 2, requestId));

            var types = await ReadFrameTypesAsync(output.Reader, context.Protocol, expectedFrames: 4);
            Ensure(types.Count == 4, $"expected 4 frames, read {types.Count}");
            Ensure(types.Take(3).All(static type => type == ProtocolV2FrameType.StreamData),
                "all stream-data frames must precede the stream-complete frame");
            Ensure(types[3] == ProtocolV2FrameType.StreamComplete,
                "the stream-complete frame must follow the stream-data frames of the same stream");
        }
        finally
        {
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task NormalFramesKeepFifoOrderUnderProgressStorm()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "normal-fifo-under-progress-storm",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        try
        {
            // Interleave a progress storm with sequenced normal frames; the
            // normal subsequence must reach the transport in enqueue order.
            for (var index = 1; index <= 40; index++)
            {
                session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Ping, 8, requestId: 0));
                if (index % 4 == 0)
                    session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Response, 64, checked((ulong)(index / 4))));
            }

            var normalOrder = await ReadResponseRequestIdsAsync(
                output.Reader, context.Protocol, expectedFrames: 10);
            Ensure(normalOrder.Count == 10, $"expected 10 response frames, read {normalOrder.Count}");
            for (var index = 0; index < normalOrder.Count; index++)
            {
                Ensure(normalOrder[index] == checked((ulong)index + 1),
                    $"response {index} must keep its enqueue position, read request id {normalOrder[index]}");
            }
        }
        finally
        {
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task ProgressBurstDoesNotStarveNormalFrames()
    {
        var clock = new ManualTimeProvider();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "progress-burst-normal-interleave",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, TimeSpan.FromSeconds(10))));
        try
        {
            // Park the pump in the timed-batch deadline wait, then queue a
            // progress storm followed by normal frames while it is blocked.
            session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Response, 128, requestId: 99));
            await WaitUntilAsync(() => clock.EarliestTimerTimestamp != long.MaxValue);

            for (var index = 0; index < 24; index++)
                session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Ping, 8, requestId: 0));
            for (var index = 1; index <= 10; index++)
                session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Response, 64, checked((ulong)index)));
            // The pump drains the whole progress backlog first, then the
            // complete normal queue; the reader advances the manual clock
            // whenever the pipe has no data so every parked batch flushes
            // deterministically.
            var types = await ReadFrameTypesWithClockAsync(
                output.Reader, context.Protocol, clock, expectedFrames: 35);
            Ensure(types.Count == 35, $"expected 35 frames, read {types.Count}");
            // Deterministic drain order after the pump wakes: the full
            // progress backlog, then all ten normal frames. Even with a
            // multi-burst progress backlog queued ahead of them, the normal
            // frames are all drained in one normal-queue pass, so bulk
            // traffic cannot starve behind protocol progress.
            Ensure(types.Skip(1).Take(24).All(static type => type == ProtocolV2FrameType.Ping),
                "the full progress backlog must drain first");
            Ensure(types.Skip(25).Take(10).All(static type => type == ProtocolV2FrameType.Response),
                "all ten normal frames must drain in one pass after the progress backlog");
        }
        finally
        {
            await session.DisposeAsync();
            await clock.WaitForTimersDrainedAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task ProgressInterleaveServesProgressWhileNormalQueueNeverEmpties()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "progress-interleave-mid-while",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        try
        {
            // With no reader the pump can write only two batches before its
            // flush blocks on the unconsumed pipe, so the progress frames
            // enqueued after the bulk frames always land while the pump is
            // inside the normal-queue drain loop.
            for (var index = 1; index <= 130; index++)
            {
                session.SendPacket(CreateFrame(
                    session, ProtocolV2FrameType.Response, 8 * 1024, checked((ulong)index)));
            }
            for (var index = 0; index < 10; index++)
                session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Ping, 8, requestId: 0));

            var types = await ReadFrameTypesAsync(output.Reader, context.Protocol, expectedFrames: 140);
            Ensure(types.Count == 140, $"expected 140 frames, read {types.Count}");
            // The pump is parked inside the normal-queue drain (its flush is
            // blocked on the unconsumed pipe), so the ten progress frames must
            // be served at the interleave boundary or, when the pump wakes
            // late, at the loop top: either way they drain as one contiguous
            // batch and are never deferred behind the entire bulk backlog.
            var pingIndices = new List<int>();
            for (var index = 0; index < types.Count; index++)
            {
                if (types[index] == ProtocolV2FrameType.Ping)
                    pingIndices.Add(index);
            }
            Ensure(pingIndices.Count == 10, $"expected 10 pings, read {pingIndices.Count}");
            Ensure(pingIndices.SequenceEqual(Enumerable.Range(pingIndices[0], 10)),
                $"the progress frames must drain as one contiguous batch (indices {string.Join(',', pingIndices)})");
            Ensure(pingIndices[9] < types.Count - 1,
                "the progress batch must flush before the final bulk frames");
            Ensure(types.Where(static type => type == ProtocolV2FrameType.Response).Count() == 130,
                "all bulk frames must be delivered around the progress batch");
        }
        finally
        {
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task ProgressHeadroomAdmitsProgressFramesWhenBulkIsFull()
    {
        const int queueBytes = 64 * 1024;
        using var context = BuildContextWithQueue(queueBytes);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "progress-headroom-admission",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        try
        {
            // Fill the normal class until admission fails; the pump is blocked
            // on transport backpressure so the queue cannot drain.
            var bulkFull = 0;
            var bulkAccepted = 0;
            for (var index = 0; index < 64; index++)
            {
                try
                {
                    session.SendPacket(CreateFrame(
                        session, ProtocolV2FrameType.Response, 8 * 1024, checked((ulong)index + 1)));
                    bulkAccepted++;
                }
                catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                    bulkFull++;
                }
            }
            Ensure(bulkFull > 0, "bulk admission must reach Full with a stalled transport");
            Ensure(bulkAccepted >= 6, $"bulk admission should accept several frames before Full (accepted {bulkAccepted})");

            // The reserved progress headroom must still admit tiny progress frames.
            var progressAccepted = 0;
            for (var index = 0; index < 32; index++)
            {
                try
                {
                    session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Ping, 8, requestId: 0));
                    progressAccepted++;
                }
                catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                }
            }
            Ensure(progressAccepted == 32,
                $"all progress frames must be admitted through the reserved headroom (accepted {progressAccepted})");
            Ensure(session.QueuedSendBytes <= queueBytes,
                "the byte hard bound must never be exceeded");
        }
        finally
        {
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task ProgressHeadroomIsBoundedByTheQueueHardLimit()
    {
        const int queueBytes = 64 * 1024;
        using var context = BuildContextWithQueue(queueBytes);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "progress-headroom-hard-limit",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        try
        {
            for (var index = 0; index < 64; index++)
            {
                try
                {
                    session.SendPacket(CreateFrame(
                        session, ProtocolV2FrameType.Response, 8 * 1024, checked((ulong)index + 1)));
                }
                catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                    break;
                }
            }

            var progressAccepted = 0;
            var progressFull = 0;
            for (var index = 0; index < 4096; index++)
            {
                try
                {
                    session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Ping, 8, requestId: 0));
                    progressAccepted++;
                }
                catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                    progressFull++;
                }
            }
            Ensure(progressAccepted > 32,
                $"progress frames must use the reserved headroom beyond the normal limit (accepted {progressAccepted})");
            Ensure(progressFull > 0,
                "progress admission must fail once the queue hard limit is reached");
            Ensure(session.QueuedSendBytes <= queueBytes,
                "the byte hard bound must never be exceeded");
        }
        finally
        {
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task OversizedNormalFrameNeverOccupiesTheProgressReserve()
    {
        const int queueBytes = 64 * 1024;
        using var context = BuildContextWithQueue(queueBytes);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "oversized-normal-frame",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        try
        {
            // Larger than the normal limit (queue minus the progress reserve):
            // even on an empty queue it must not be admitted, otherwise it
            // would consume the reserve and block liveness frames while the
            // transport drains it.
            var payloadBytes = queueBytes - 2048;
            var oversizedWasFull = false;
            try
            {
                session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Response, payloadBytes, requestId: 1));
            }
            catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
            {
                oversizedWasFull = true;
            }
            Ensure(oversizedWasFull,
                "an oversized normal frame must fail admission even on an empty queue");

            // A progress frame still fits the full queue budget, and a normal
            // frame within the normal limit is still admitted.
            session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Ping, 8, requestId: 0));
            session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Response, 1024, requestId: 2));
        }
        finally
        {
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task TransportFaultDrainsBothClassesAndReturnsOwners()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "fault-drains-both-classes",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        var frames = new List<IRpcByteBufferWriter>();
        try
        {
            for (var index = 0; index < 32; index++)
            {
                var frame = CreateFrame(session, ProtocolV2FrameType.Response, 128, checked((ulong)index + 1));
                frames.Add(frame);
                session.SendPacket(frame);
            }
            var progress = CreateFrame(session, ProtocolV2FrameType.Ping, 8, requestId: 0);
            frames.Add(progress);
            session.SendPacket(progress);

            output.Writer.Complete(new IOException("transport failed"));
            await WaitUntilAsync(() => session.QueuedSendBytes == 0);
            Ensure(session.QueuedSendBytes == 0,
                "a transport fault must drain both queues and release all reserved bytes");
            foreach (var frame in frames)
                EnsureReturned(frame, "every queued frame owner must be returned on a transport fault");
        }
        finally
        {
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task StopDrainsBothClassesAndCompletesCapacityWaiters()
    {
        const int queueBytes = 64 * 1024;
        using var context = BuildContextWithQueue(queueBytes);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "stop-drains-both-classes",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        try
        {
            for (var index = 0; index < 64; index++)
            {
                try
                {
                    session.SendPacket(CreateFrame(
                        session, ProtocolV2FrameType.Response, 8 * 1024, checked((ulong)index + 1)));
                }
                catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                    break;
                }
            }

            // A large waiter frame cannot squeeze into the headroom-limited
            // normal budget, so the backpressure waiter must park.
            var waiterFrame = CreateFrame(session, ProtocolV2FrameType.Response, 8 * 1024, requestId: 99);
            var waiter = Task.Run(async () => await session
                .SendPacketWithBackpressureAsync(waiterFrame)
                .ConfigureAwait(false));

            await session.DisposeAsync();
            var waiterException = await CaptureExceptionAsync(() => waiter);
            Ensure(waiterException is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
                "a capacity waiter must complete with a connection-closed error when the pump stops");
            Ensure(session.QueuedSendBytes == 0,
                "stopping must drain both queues and release all reserved bytes");
        }
        finally
        {
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task ProgressFrameInterruptsTimedBatchDeadline()
    {
        var clock = new ManualTimeProvider();
        var maxLatency = TimeSpan.FromSeconds(5);
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "progress-interrupts-deadline",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, maxLatency)));
        try
        {
            session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Response, 128, requestId: 1));
            await WaitUntilAsync(() => clock.EarliestTimerTimestamp != long.MaxValue);
            Ensure(clock.EarliestTimerTimestamp == maxLatency.Ticks,
                "the first normal frame must arm the batch deadline");

            // The progress frame must end the batching window without waiting
            // for the manual clock to reach the deadline.
            session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Ping, 8, requestId: 0));

            var types = await ReadFrameTypesAsync(output.Reader, context.Protocol, expectedFrames: 2);
            Ensure(types.Count == 2, $"expected 2 frames, read {types.Count}");
            // The normal frame was already staged into the transport pipe when
            // the progress frame arrived, so it cannot be un-written; what the
            // interruption guarantees is the flush itself before the deadline.
            Ensure(types[0] == ProtocolV2FrameType.Response,
                "the already-staged normal frame keeps its transport position");
            Ensure(types[1] == ProtocolV2FrameType.Ping,
                "the interrupting progress frame must flush with the batch before the deadline");
            Ensure(clock.ActiveTimerCount == 0,
                "the progress interruption must dispose the batch deadline timer");
        }
        finally
        {
            await session.DisposeAsync();
            await clock.WaitForTimersDrainedAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task HundredThousandMixedFramesKeepByteAccountingBalanced()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "mixed-100k-accounting",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        // Drain the transport so the pump stays busy while 100k mixed-class
        // frames flow through both queues.
        var drain = Task.Run(async () =>
        {
            while (true)
            {
                var result = await output.Reader.ReadAsync().AsTask();
                output.Reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                    return;
            }
        });
        try
        {
            for (var index = 0; index < 100_000; index++)
            {
                if ((index & 3) == 0)
                {
                    session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Ping, 8, requestId: 0));
                }
                else
                {
                    try
                    {
                        session.SendPacket(CreateFrame(
                            session, ProtocolV2FrameType.Response, 64, checked((ulong)index)));
                    }
                    catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
                    {
                        // A producer that cannot wait must tolerate transient Full.
                        await Task.Delay(1);
                    }
                }
                if (session.QueuedSendBytes < 0)
                    throw new Exception("queued bytes went negative during the mixed stress");
            }

            await session.FlushSendQueueAsync();
            await WaitUntilAsync(() => session.QueuedSendBytes == 0);
            Ensure(session.QueuedSendBytes == 0,
                "all reserved bytes must be released after the mixed stress drains");
        }
        finally
        {
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
            await output.Writer.CompleteAsync();
            await drain;
        }
    }

    [Test]
    public void ProgressFlagDoesNotGrowOwnedFrame()
    {
        // The progress-class flag must fit the existing padding: the struct
        // must stay exactly the size of its pre-flag shape.
        Ensure(Unsafe.SizeOf<OwnedFrame>() == Unsafe.SizeOf<OwnedFrameWithoutProgressFlag>(),
            $"OwnedFrame must not grow with the progress flag " +
            $"(measured {Unsafe.SizeOf<OwnedFrame>()} vs {Unsafe.SizeOf<OwnedFrameWithoutProgressFlag>()})");
    }

    /// <summary>The OwnedFrame shape before the protocol-progress flag existed.</summary>
    private readonly struct OwnedFrameWithoutProgressFlag(
        IRpcByteBufferWriter owner,
        bool forceFlush,
        TaskCompletionSource<bool>? flushCompletion)
    {
        public IRpcByteBufferWriter Owner { get; } = owner;
        public ReadOnlyMemory<byte> Memory { get; } = owner.WrittenMemory;
        public int Length { get; } = owner.WrittenCount;
        public bool ForceFlush { get; } = forceFlush;
        public TaskCompletionSource<bool>? FlushCompletion { get; } = flushCompletion;
    }

    // ----- helpers -------------------------------------------------------

    private static SharpLinkRuntimeContext BuildContextWithQueue(int queueBytes)
        => new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.FlowControl.MaxSendQueueBytes = queueBytes)
            .Build(includeGeneratedAssemblyCatalog: false);

    private static IRpcByteBufferWriter CreateFrame(
        RpcSession session,
        ProtocolV2FrameType frameType,
        int payloadBytes,
        ulong requestId)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
        using (writer.BeginPacketScope(frameType, ProtocolV2FrameFlags.None, requestId))
        {
            writer.Write(new byte[payloadBytes]);
        }
        return writer;
    }

    private static async Task<List<ProtocolV2FrameType>> ReadFrameTypesAsync(
        PipeReader reader,
        SharpLinkProtocolOptions limits,
        int expectedFrames)
    {
        var types = new List<ProtocolV2FrameType>();
        while (types.Count < expectedFrames)
        {
            var result = await reader.ReadAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10));
            var buffer = result.Buffer;
            while (ProtocolV2FrameParser.TryReadFrame(ref buffer, limits, out var header, out _))
                types.Add(header.Type);
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted && buffer.Length == 0)
                break;
        }
        return types;
    }

    private static async Task<List<ProtocolV2FrameType>> ReadFrameTypesWithClockAsync(
        PipeReader reader,
        SharpLinkProtocolOptions limits,
        ManualTimeProvider clock,
        int expectedFrames)
    {
        var types = new List<ProtocolV2FrameType>();
        for (var attempt = 0; attempt < 64 && types.Count < expectedFrames; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                var result = await reader.ReadAsync(timeout.Token).AsTask();
                var buffer = result.Buffer;
                while (ProtocolV2FrameParser.TryReadFrame(ref buffer, limits, out var header, out _))
                    types.Add(header.Type);
                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted && buffer.Length == 0)
                    break;
            }
            catch (OperationCanceledException)
            {
                // No data yet: the pump is parked in a deadline wait — advance
                // the manual clock so the parked batch flushes.
                clock.Advance(TimeSpan.FromSeconds(10));
            }
        }
        return types;
    }

    private static async Task<List<ulong>> ReadResponseRequestIdsAsync(
        PipeReader reader,
        SharpLinkProtocolOptions limits,
        int expectedFrames)
    {
        var requestIds = new List<ulong>();
        while (requestIds.Count < expectedFrames)
        {
            var result = await reader.ReadAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10));
            var buffer = result.Buffer;
            while (ProtocolV2FrameParser.TryReadFrame(ref buffer, limits, out var header, out _))
            {
                if (header.Type == ProtocolV2FrameType.Response)
                    requestIds.Add(header.RequestId);
            }
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted && buffer.Length == 0)
                break;
        }
        return requestIds;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("condition was not reached");
            await Task.Delay(5);
        }
    }

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
