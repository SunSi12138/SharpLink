using System.Linq;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;

namespace SharpLink.UnitTests.Runtime;

public class EncodedStreamPageTransportTests
{
    [Test]
    public async Task AdjacentFramesFromOnePageUseOnePhysicalWriteWithoutPipelineCopy()
    {
        using var context = CreateContext();
        var stream = new RecordingStream();
        var transport = new PageTransport(stream);
        var session = CreateSession(context, transport);
        var bytes = EncodeFrame(session, 1).Concat(EncodeFrame(session, 2)).ToArray();
        var returned = 0;
        var page = new EncodedStreamPage(bytes, _ => Interlocked.Increment(ref returned));
        var completion = Completion();
        try
        {
            Publish(session, page, 0, bytes.Length / 2);
            Publish(session, page, bytes.Length / 2, bytes.Length / 2, completion);
            page.Release();
            await completion.Task;
            Ensure(transport.DirectWrites == 1 && stream.Writes.Count == 0 && stream.Flushes == 0,
                "one adjacent page batch must use exactly one direct write and no pipeline flush");
            Ensure(MemoryMarshal.TryGetArray(transport.LastMemory, out var sent) &&
                ReferenceEquals(sent.Array, bytes) && sent.Offset == 0 && sent.Count == bytes.Length,
                "direct transport must receive the original page slice, not a copied array");
            Ensure(returned == 1 && session.QueuedSendBytes == 0,
                "physical completion must return the sole page once and release all per-frame credits");
        }
        finally { await session.DisposeAsync(); }
    }

    [Test]
    [Arguments("fragmented")]
    [Arguments("different-page")]
    [Arguments("legacy")]
    [Arguments("marker")]
    public async Task IneligibleWholeBatchRetainsSingleLegacyFlush(string shape)
    {
        using var context = CreateContext();
        var stream = new RecordingStream();
        var transport = new PageTransport(stream);
        var session = CreateSession(context, transport);
        var first = EncodeFrame(session, 1);
        var second = EncodeFrame(session, 2);
        var gap = shape == "fragmented" ? 1 : 0;
        var bytes = new byte[first.Length + gap + second.Length];
        first.CopyTo(bytes, 0);
        second.CopyTo(bytes, first.Length + gap);
        var returned = 0;
        var page = new EncodedStreamPage(bytes, _ => Interlocked.Increment(ref returned));
        EncodedStreamPage? other = null;
        try
        {
            Publish(session, page, 0, first.Length);
            Task completed;
            if (shape == "legacy")
                completed = session.SendPacketAndFlushAsync(CreateWriter(session, 2)).AsTask();
            else if (shape == "marker")
                completed = session.FlushSendQueueAsync().AsTask();
            else
            {
                var completion = Completion();
                if (shape == "different-page")
                {
                    // The same underlying array is not the same page generation.
                    other = new EncodedStreamPage(bytes, _ => Interlocked.Increment(ref returned));
                    Publish(session, other, first.Length, second.Length, completion);
                }
                else
                    Publish(session, page, first.Length + gap, second.Length, completion);
                completed = completion.Task;
            }
            page.Release();
            other?.Release();
            await completed;
            var expected = shape == "marker" ? first : first.Concat(second).ToArray();
            Ensure(transport.DirectWrites == 0 && stream.Writes.Count == 1 && stream.Flushes == 1,
                "mixed, fragmented or empty-marker batches must stay one legacy write and flush");
            Ensure(stream.Writes[0].SequenceEqual(expected), "fallback must preserve every frame byte and order");
            Ensure(returned == (other is null ? 1 : 2) && session.QueuedSendBytes == 0,
                "fallback must release each held page reference and byte reservation");
        }
        finally { await session.DisposeAsync(); }
    }

    [Test]
    public async Task TransportWithoutCapabilityKeepsPageBatchOnLegacyPath()
    {
        using var context = CreateContext();
        var stream = new RecordingStream();
        var session = CreateSession(context, new StreamTransportConnection(stream));
        var bytes = EncodeFrame(session, 1);
        var returned = 0;
        var page = new EncodedStreamPage(bytes, _ => Interlocked.Increment(ref returned));
        try
        {
            var completion = Completion();
            Publish(session, page, 0, bytes.Length, completion);
            page.Release();
            await completion.Task;
            Ensure(stream.Writes.Count == 1 && stream.Flushes == 1 && stream.Writes[0].SequenceEqual(bytes),
                "custom stream transports must retain their existing pipeline output contract");
            Ensure(returned == 1, "legacy transport completion must release the page reference");
        }
        finally { await session.DisposeAsync(); }
    }

    [Test]
    public async Task PendingLegacyFlushPhysicallyFinishesBeforeDirectWriteStarts()
    {
        using var context = CreateContext();
        var stream = new RecordingStream { HoldWrite = true };
        var transport = new PageTransport(stream);
        var session = CreateSession(context, transport);
        var bytes = EncodeFrame(session, 2);
        var page = new EncodedStreamPage(bytes, _ => { });
        var completion = Completion();
        var legacy = session.SendPacketAndFlushAsync(CreateWriter(session, 1)).AsTask();
        try
        {
            await stream.WriteEntered.Task;
            Publish(session, page, 0, bytes.Length, completion);
            page.Release();
            Ensure(transport.DirectWrites == 0 && !completion.Task.IsCompleted,
                "page output must not overtake a physically incomplete legacy flush");
            stream.ReleaseWrite.TrySetResult();
            await legacy;
            await completion.Task;
            Ensure(stream.Flushes == 1 && transport.DirectWrites == 1 && !transport.OverlappedLegacyWrite,
                "the sole pump must serialize legacy flush and direct write");
        }
        finally
        {
            stream.ReleaseWrite.TrySetResult();
            await session.DisposeAsync();
        }
    }

    [Test]
    public async Task HeldDirectWriteRetainsPageAndBlocksFollowingLegacyOutputThroughCancellation()
    {
        using var context = CreateContext();
        var stream = new RecordingStream();
        var transport = new PageTransport(stream) { HoldDirectWrite = true };
        var session = CreateSession(context, transport);
        var bytes = EncodeFrame(session, 1);
        var returned = 0;
        var page = new EncodedStreamPage(bytes, _ => Interlocked.Increment(ref returned));
        var completion = Completion();
        try
        {
            Publish(session, page, 0, bytes.Length, completion);
            page.Release();
            await transport.DirectEntered.Task;
            var legacy = session.SendPacketAndFlushAsync(CreateWriter(session, 2)).AsTask();
            var disposing = session.DisposeAsync().AsTask();
            Ensure(transport.WriteToken.IsCancellationRequested && !disposing.IsCompleted,
                "session cancellation must not detach the actual physical write");
            Ensure(returned == 0 && !completion.Task.IsCompleted && session.QueuedSendBytes >= bytes.Length,
                "held direct write must retain its page reference, waiter and queue credit");
            Ensure(stream.Writes.Count == 0, "following legacy output must not overlap the direct write");
            transport.ReleaseDirect.TrySetResult();
            await completion.Task;
            _ = await CaptureAsync(legacy);
            await disposing;
            Ensure(returned == 1 && session.QueuedSendBytes == 0,
                "physical termination must release page ownership and queue credits exactly once");
        }
        finally
        {
            transport.ReleaseDirect.TrySetResult();
            await session.DisposeAsync();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DirectWriteFailureNeverReplaysAndDrainsQueuedOwners(bool partial)
    {
        using var context = CreateContext();
        var stream = new RecordingStream();
        var transport = new PageTransport(stream)
        {
            HoldDirectWrite = true,
            FailDirectWrite = true,
            PartialBytes = partial ? 7 : 0
        };
        var session = CreateSession(context, transport);
        var bytes = EncodeFrame(session, 1).Concat(EncodeFrame(session, 2)).ToArray();
        var returned = 0;
        var page = new EncodedStreamPage(bytes, _ => Interlocked.Increment(ref returned));
        var first = Completion();
        var second = Completion();
        try
        {
            Publish(session, page, 0, bytes.Length / 2, first);
            await transport.DirectEntered.Task;
            Publish(session, page, bytes.Length / 2, bytes.Length / 2, second);
            page.Release();
            Ensure(returned == 0, "queued and in-flight slices must hold the page until physical failure");
            transport.ReleaseDirect.TrySetResult();
            var failure = await CaptureAsync(first.Task);
            var queuedFailure = await CaptureAsync(second.Task);
            await session.DisposeAsync();
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed } &&
                ReferenceEquals(failure, queuedFailure), "all accepted slices must observe the same terminal failure");
            Ensure(transport.DirectWrites == 1 && transport.AttemptedBytes == bytes.Length / 2 &&
                transport.WrittenBytes == (partial ? 7 : 0) && stream.Writes.Count == 0 && stream.Flushes == 0,
                "neither the failed direct write nor queued frames may be retried or replayed during disposal");
            Ensure(returned == 1 && session.QueuedSendBytes == 0 && stream.DisposeCount == 1,
                "terminal cleanup must release every slice, queue credit and transport exactly once");
        }
        finally
        {
            transport.ReleaseDirect.TrySetResult();
            await session.DisposeAsync();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DirectOutputCountsAttemptedBytesExactlyOnceBeforePhysicalWrite(bool partialFailure)
    {
        var scope = new AsyncLocal<bool>();
        var measurements = 0;
        var attemptedBytes = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, owner) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name == "sharplink.transport.bytes.sent")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            // Only this pump inherits this AsyncLocal; unrelated parallel test sessions
            // cannot contaminate the process-wide instrument's measurement window.
            if (!scope.Value) return;
            Interlocked.Increment(ref measurements);
            Interlocked.Add(ref attemptedBytes, value);
        });
        listener.Start();
        using var context = CreateContext();
        var stream = new RecordingStream();
        var transport = new PageTransport(stream)
        {
            HoldDirectWrite = true,
            FailDirectWrite = partialFailure,
            PartialBytes = 7
        };
        var session = CreateSession(context, transport);
        var bytes = EncodeFrame(session, 1).Concat(EncodeFrame(session, 2)).ToArray();
        var page = new EncodedStreamPage(bytes, _ => { });
        var completion = Completion();
        scope.Value = true;
        try
        {
            Publish(session, page, 0, bytes.Length / 2);
            Publish(session, page, bytes.Length / 2, bytes.Length / 2, completion);
            page.Release();
            await transport.DirectEntered.Task;
            Ensure(measurements == 1 && attemptedBytes == bytes.Length && transport.WrittenBytes == 0,
                "sent-byte telemetry must count the whole attempted batch exactly once before physical output");
            transport.ReleaseDirect.TrySetResult();
            var failure = await CaptureAsync(completion.Task);
            await session.DisposeAsync();
            Ensure((failure is not null) == partialFailure && measurements == 1 && attemptedBytes == bytes.Length,
                "success or partial failure and cleanup must neither recount nor subtract attempted bytes");
            Ensure(transport.DirectWrites == 1 && stream.Writes.Count == 0 && stream.Flushes == 0,
                "telemetry must describe one direct write, with no fallback copy/write or replay");
        }
        finally
        {
            transport.ReleaseDirect.TrySetResult();
            await session.DisposeAsync();
            scope.Value = false;
        }
    }

    [Test]
    [Arguments(SharpLinkPerformanceProfile.LowLatency, 4)]
    [Arguments(SharpLinkPerformanceProfile.Throughput, 2)]
    public async Task PageOutputPreservesProfileBatchBoundaries(SharpLinkPerformanceProfile profile, int expectedWrites)
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.PerformanceProfile = profile)
            .Build(includeGeneratedAssemblyCatalog: false);
        var stream = new RecordingStream();
        var transport = new PageTransport(stream) { HoldDirectWrite = true };
        var session = new RpcSession(transport, RpcSessionTestFixture.ServerOptions(context));
        RpcSessionTestFixture.CompleteHandshake(session);
        var frameBytes = EncodeFrame(session, 1);
        var bytes = Enumerable.Range(0, 4).SelectMany(_ => frameBytes).ToArray();
        var returned = 0;
        var page = new EncodedStreamPage(bytes, _ => Interlocked.Increment(ref returned));
        var first = Completion();
        var last = Completion();
        try
        {
            Publish(session, page, 0, frameBytes.Length, first);
            await transport.DirectEntered.Task;
            // Fill the normal queue behind a real held write: Throughput sees one whole
            // adjacent batch, while LowLatency must retain its per-frame flush boundaries.
            Publish(session, page, frameBytes.Length, frameBytes.Length);
            Publish(session, page, 2 * frameBytes.Length, frameBytes.Length);
            Publish(session, page, 3 * frameBytes.Length, frameBytes.Length, last);
            page.Release();
            transport.ReleaseDirect.TrySetResult();
            await first.Task;
            await last.Task;
            Ensure(transport.DirectWrites == expectedWrites && stream.Writes.Count == 0 && stream.Flushes == 0,
                "the direct path must honor the existing profile boundaries without pipeline copies");
            Ensure(transport.AttemptedBytes == bytes.Length && returned == 1 && session.QueuedSendBytes == 0,
                "every profile must preserve total wire bytes, item admission credits and page lifetime");
        }
        finally
        {
            transport.ReleaseDirect.TrySetResult();
            await session.DisposeAsync();
        }
    }

    [Test]
    public async Task LiveFlushPolicyDecreaseWakesRetainedEligiblePageBatch()
    {
        var clock = new ManualTimeProvider();
        var initial = new RpcSessionFlushOptions(1024 * 1024, TimeSpan.FromSeconds(30));
        using var context = new SharpLinkRuntimeContextBuilder().UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var owner = CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy());
        var policy = owner.GetOrCreateSessionFlushPolicyState(initial, context.PerformanceProfile);
        var stream = new RecordingStream();
        var transport = new PageTransport(stream);
        var session = new RpcSession(transport,
            new RpcSessionCreationOptions(RpcSessionRole.Server, context, initial, owner));
        RpcSessionTestFixture.CompleteHandshake(session);
        var bytes = EncodeFrame(session, 1);
        var returned = 0;
        var page = new EncodedStreamPage(bytes, _ => Interlocked.Increment(ref returned));
        var completion = Completion();
        try
        {
            page.AddRef();
            session.PublishStreamPageFrame(new OwnedFrame(page, 0, bytes.Length,
                forceFlush: false, flushCompletion: completion));
            page.Release();
            await WaitUntilAsync(() => clock.ActiveTimerCount > 0);
            Ensure(transport.DirectWrites == 0 && returned == 0 && session.QueuedSendBytes == bytes.Length,
                "the old explicit policy must retain the page and its exact item reservation");
            Ensure(policy.Publish(1, TimeSpan.FromSeconds(30)), "threshold decrease must publish a new generation");
            await completion.Task;
            Ensure(transport.DirectWrites == 1 && stream.Writes.Count == 0 && stream.Flushes == 0,
                "a live threshold change must wake and directly emit the retained page batch");
            Ensure(returned == 1 && session.QueuedSendBytes == 0,
                "the policy-triggered physical write must release its page and item credits");
        }
        finally
        {
            await session.DisposeAsync();
            await clock.WaitForTimersDrainedAsync();
        }
    }

    [Test]
    public async Task ProgressInterleaveMakesOnlyItsMixedBatchFallbackWithoutSplittingWrites()
    {
        using var context = CreateContext();
        var stream = new RecordingStream();
        var transport = new PageTransport(stream) { HoldDirectWrite = true };
        var session = CreateSession(context, transport);
        var frameBytes = EncodeFrame(session, 1);
        var bytes = Enumerable.Range(0, 66).SelectMany(_ => frameBytes).ToArray();
        var returned = 0;
        var page = new EncodedStreamPage(bytes, _ => Interlocked.Increment(ref returned));
        var first = Completion();
        var last = Completion();
        try
        {
            Publish(session, page, 0, frameBytes.Length, first);
            await transport.DirectEntered.Task;
            for (var index = 1; index < 66; index++)
                Publish(session, page, index * frameBytes.Length, frameBytes.Length, index == 65 ? last : null);
            var progress = session.RuntimeContext.Buffers.Rent();
            using (progress.BeginPacketScope(ProtocolV2FrameType.Ping, ProtocolV2FrameFlags.None, 0))
                progress.Write(new byte[8]);
            var progressBytes = progress.WrittenMemory.ToArray();
            session.SendPacket(progress);
            page.Release();
            transport.ReleaseDirect.TrySetResult();
            await first.Task;
            await last.Task;
            // The first normal frame established a direct physical-write barrier. At the
            // existing 64-frame interleave, 63 following page frames and Ping form one
            // mixed fallback batch. The final two contiguous frames are direct again.
            Ensure(transport.DirectWrites == 2 && stream.Writes.Count == 1 && stream.Flushes == 1,
                "progress interleave must keep exactly the original three physical batch boundaries");
            var expectedMixed = Enumerable.Range(0, 63).SelectMany(_ => frameBytes).Concat(progressBytes).ToArray();
            Ensure(stream.Writes[0].SequenceEqual(expectedMixed),
                "progress must follow already-staged normal frames in one coalesced fallback write");
            Ensure(transport.AttemptedBytes == 3 * frameBytes.Length && returned == 1 && session.QueuedSendBytes == 0,
                "direct and fallback batches must collectively preserve all frame bytes and release each credit");
        }
        finally
        {
            transport.ReleaseDirect.TrySetResult();
            await session.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException("The expected pump phase was not reached.");
            await Task.Delay(1);
        }
    }

    private static SharpLinkRuntimeContext CreateContext()
        => new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);

    private static RpcSession CreateSession(SharpLinkRuntimeContext context, ITransportConnection transport)
    {
        var session = new RpcSession(transport, RpcSessionTestFixture.ServerOptions(
            context, new RpcSessionFlushOptions(1024 * 1024, TimeSpan.MaxValue)));
        RpcSessionTestFixture.CompleteHandshake(session);
        return session;
    }

    private static IRpcByteBufferWriter CreateWriter(RpcSession session, ulong requestId)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
        using (writer.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, requestId))
            writer.Write(new byte[32]);
        return writer;
    }

    private static byte[] EncodeFrame(RpcSession session, ulong requestId)
    {
        var writer = CreateWriter(session, requestId);
        var bytes = writer.WrittenMemory.ToArray();
        session.RuntimeContext.Buffers.Return(writer);
        return bytes;
    }

    private static TaskCompletionSource<bool> Completion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Publish(
        RpcSession session, EncodedStreamPage page, int offset, int length,
        TaskCompletionSource<bool>? completion = null)
    {
        page.AddRef();
        session.PublishStreamPageFrame(new OwnedFrame(page, offset, length,
            forceFlush: completion is not null, flushCompletion: completion));
    }

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try { await task; return null; }
        catch (Exception exception) { return exception; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class PageTransport(RecordingStream stream)
        : StreamTransportConnection(stream), IStreamPageTransport
    {
        internal readonly TaskCompletionSource DirectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseDirect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldDirectWrite { get; init; }
        internal bool FailDirectWrite { get; init; }
        internal int PartialBytes { get; init; }
        internal int DirectWrites { get; private set; }
        internal int AttemptedBytes { get; private set; }
        internal int WrittenBytes { get; private set; }
        internal bool OverlappedLegacyWrite { get; private set; }
        internal ReadOnlyMemory<byte> LastMemory { get; private set; }
        internal CancellationToken WriteToken { get; private set; }

        public async ValueTask WriteStreamPageAsync(ReadOnlyMemory<byte> memory, CancellationToken token)
        {
            DirectWrites++;
            AttemptedBytes += memory.Length;
            OverlappedLegacyWrite |= stream.ActiveWrite;
            LastMemory = memory;
            WriteToken = token;
            DirectEntered.TrySetResult();
            if (HoldDirectWrite)
                await ReleaseDirect.Task; // Deliberately ignores logical cancellation.
            WrittenBytes += FailDirectWrite ? PartialBytes : memory.Length;
            if (FailDirectWrite)
                throw new IOException("synthetic direct write failure");
        }
    }

    private sealed class RecordingStream : Stream
    {
        internal readonly List<byte[]> Writes = [];
        internal readonly TaskCompletionSource WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldWrite { get; init; }
        internal bool ActiveWrite { get; private set; }
        internal int Flushes { get; private set; }
        internal int DisposeCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => Flushes++;
        public override Task FlushAsync(CancellationToken cancellationToken) { Flush(); return Task.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override void Write(byte[] buffer, int offset, int count) => Writes.Add(buffer.AsSpan(offset, count).ToArray());
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ActiveWrite = true;
            WriteEntered.TrySetResult();
            if (HoldWrite) await ReleaseWrite.Task;
            Writes.Add(buffer.ToArray());
            ActiveWrite = false;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
