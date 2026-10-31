using System.IO.Pipelines;
using System.Buffers.Binary;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpLink.UnitTests.Runtime;

public class CompressionFrameTests
{
    [Test]
    public async Task RequestAndStreamPayloadPrefixesShouldRemainUncompressed()
    {
        var provider = new TestCompressionProvider();
        await using var session = CreateSession(provider);
        var source = Enumerable.Repeat((byte)0x4c, 4096).ToArray();
        var compressed = Compress(provider, source);

        var requestWire = new PooledByteBufferWriter();
        requestWire.Write(new byte[ProtocolV2Constants.RequestPrefixBytes]);
        WriteOriginalLength(requestWire, source.Length);
        requestWire.Write(compressed);
        var decodedRequest = session.DecodeInboundPayload(
            ProtocolV2FrameType.Request,
            ProtocolV2FrameFlags.Compressed,
            CreateSegmented(requestWire.WrittenMemory.ToArray(), 19),
            CancellationToken.None,
            out var requestOwner);
        try
        {
            Ensure(decodedRequest.Length == ProtocolV2Constants.RequestPrefixBytes + source.Length,
                "request decoded length");
            Ensure(decodedRequest.Slice(ProtocolV2Constants.RequestPrefixBytes).ToArray().SequenceEqual(source),
                "request business payload");
        }
        finally
        {
            session.ReturnDecodedPayload(requestOwner);
        }

        var streamWire = new PooledByteBufferWriter();
        var streamId = streamWire.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(streamId, 7);
        streamWire.Advance(sizeof(ushort));
        WriteOriginalLength(streamWire, source.Length);
        streamWire.Write(compressed);
        var decodedStream = session.DecodeInboundPayload(
            ProtocolV2FrameType.StreamData,
            ProtocolV2FrameFlags.Compressed,
            new ReadOnlySequence<byte>(streamWire.WrittenMemory),
            CancellationToken.None,
            out var streamOwner);
        try
        {
            Ensure(BinaryPrimitives.ReadUInt16LittleEndian(decodedStream.FirstSpan) == 7,
                "stream ID prefix");
            Ensure(decodedStream.Slice(sizeof(ushort)).ToArray().SequenceEqual(source),
                "stream business payload");
        }
        finally
        {
            session.ReturnDecodedPayload(streamOwner);
        }
    }


    [Test]
    public async Task ZeroByteRepresentationShouldRoundTripThroughWireEnvelope()
    {
        var provider = new ZeroBodyCompressionProvider();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Compression.Providers.Add(provider))
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        var observedWriter = new ObservedPipeWriter(output.Writer);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "compression-zero-body",
            input.Reader,
            observedWriter,
            RpcSessionTestFixture.ClientOptions(context),
            completeHandshake: false);
        Task? flushTask = null;
        Exception? primaryFailure = null;
        try
        {
            RpcSessionTestFixture.CompleteHandshake(
                session,
                ProtocolV2Capabilities.Compression,
                compressionBinding: context.Compression.ProviderBindings[0]);

            var original = new byte[2048];
            var frame = context.Buffers.Rent(ProtocolV2Constants.HeaderBytes + original.Length);
            var frameToken = ProtocolV2FrameWriter.BeginFrame(
                frame,
                ProtocolV2FrameType.Response,
                ProtocolV2FrameFlags.None,
                requestId: 1);
            frame.Write(original);
            ProtocolV2FrameWriter.EndFrame(frame, frameToken);

            observedWriter.Record("before-send");
            session.SendPacket(frame);
            observedWriter.Record("send-returned");
            flushTask = session.FlushSendQueueAsync().AsTask();
            observedWriter.Record("flush-request-returned");
            await flushTask.WaitAsync(TimeSpan.FromSeconds(2));
            observedWriter.Record("flush-marker-completed");

            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            try
            {
                var wire = read.Buffer;
                Ensure(ProtocolV2FrameParser.TryReadFrame(
                    ref wire,
                    session.RuntimeContext.Protocol,
                    out var header,
                    out var payload), "compressed response frame should be published");
                Ensure((header.Flags & ProtocolV2FrameFlags.Compressed) != 0,
                    "zero-byte representation should remain a compressed frame");
                Ensure(payload.Length == sizeof(uint),
                    "zero-byte representation should contain only the original-length envelope");

                var decoded = session.DecodeInboundPayload(
                    header.Type,
                    header.Flags,
                    payload,
                    CancellationToken.None,
                    out var owner);
                try
                {
                    Ensure(decoded.Length == original.Length, "zero-byte representation decoded length");
                    Ensure(decoded.ToArray().SequenceEqual(original), "zero-byte representation round-trip payload");
                    observedWriter.Record("wire-round-trip-verified");
                }
                finally
                {
                    session.ReturnDecodedPayload(owner);
                }
            }
            finally
            {
                output.Reader.AdvanceTo(read.Buffer.End);
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            observedWriter.Snapshot("failure-before-cleanup", session, provider, flushTask);
            observedWriter.Dump();
            throw;
        }
        finally
        {
            observedWriter.Record("cleanup-entered");
            Exception? cleanupFailure = null;
            await Cleanup("output-reader", () => output.Reader.CompleteAsync());
            await Cleanup("input-writer", () => input.Writer.CompleteAsync());
            await Cleanup("session", () => session.DisposeAsync());
            if (flushTask is { } pendingFlush)
                await Cleanup("flush-marker", () => new ValueTask(pendingFlush));
            observedWriter.Snapshot("cleanup-finished", session, provider, flushTask);
            observedWriter.Dump();
            if (cleanupFailure is not null && primaryFailure is null)
                throw cleanupFailure;

            async Task Cleanup(string name, Func<ValueTask> action)
            {
                observedWriter.Record($"cleanup-{name}-started");
                observedWriter.Dump();
                try
                {
                    await action();
                    observedWriter.Record($"cleanup-{name}-completed");
                }
                catch (Exception exception)
                {
                    observedWriter.Record($"cleanup-{name}-failed:{exception}");
                    cleanupFailure = cleanupFailure is null
                        ? exception : new AggregateException(cleanupFailure, exception);
                }
            }
        }
    }

    [Test]
    [Arguments("truncated")]
    [Arguments("corrupt")]
    [Arguments("trailing")]
    public async Task InvalidCompressedBodyShouldMapToDataLoss(string mutation)
    {
        var provider = new TestCompressionProvider();
        await using var session = CreateSession(provider);
        var source = Enumerable.Repeat((byte)0x6d, 4096).ToArray();
        var compressed = Compress(provider, source).ToList();
        switch (mutation)
        {
            case "truncated":
                compressed.RemoveAt(compressed.Count - 1);
                break;
            case "corrupt":
                compressed[compressed.Count / 2] ^= 0x80;
                break;
            case "trailing":
                compressed.Add(0xff);
                break;
        }

        using var wire = new PooledByteBufferWriter();
        WriteOriginalLength(wire, source.Length);
        wire.Write(compressed.ToArray());
        var exception = CaptureSharpLinkException(() => session.DecodeInboundPayload(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.Compressed,
            new ReadOnlySequence<byte>(wire.WrittenMemory),
            CancellationToken.None,
            out _));
        Ensure(exception.Code == SharpLinkErrorCode.DataLoss, $"{mutation} maps to DataLoss");
    }

    [Test]
    public async Task OriginalLengthShouldBeValidatedBeforeRentingOutput()
    {
        var provider = new TestCompressionProvider();
        await using var session = CreateSession(provider);
        using var wire = new PooledByteBufferWriter();
        var length = wire.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(length, uint.MaxValue);
        wire.Advance(sizeof(uint));
        wire.Write(new byte[] { 1 });

        var exception = CaptureSharpLinkException(() => session.DecodeInboundPayload(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.Compressed,
            new ReadOnlySequence<byte>(wire.WrittenMemory),
            CancellationToken.None,
            out _));
        Ensure(exception.Code == SharpLinkErrorCode.ProtocolViolation, "uint32 original length bound");
    }

    [Test]
    public async Task UnnegotiatedCompressedFrameShouldBeProtocolViolation()
    {
        var provider = new TestCompressionProvider();
        await using var session = CreateSession(provider, enableCompression: false);
        var exception = CaptureSharpLinkException(() => session.DecodeInboundPayload(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.Compressed,
            new ReadOnlySequence<byte>(new byte[5]),
            CancellationToken.None,
            out _));
        Ensure(exception.Code == SharpLinkErrorCode.ProtocolViolation, "unnegotiated compressed frame");
    }

    [Test]
    public async Task OversizedOriginalPayloadShouldBeRejectedBeforeCompression()
    {
        var provider = new ThrowIfCompressedProvider();
        await using var session = CreateSession(
            provider,
            maxFramePayloadBytes: SharpLinkProtocolOptions.MinMaxFramePayloadBytes);
        var writer = session.RuntimeContext.Buffers.Rent(
            ProtocolV2Constants.HeaderBytes + SharpLinkProtocolOptions.MinMaxFramePayloadBytes + 1);
        using (writer.BeginPacketScope(
                   ProtocolV2FrameType.Response,
                   ProtocolV2FrameFlags.None,
                   requestId: 1))
        {
            writer.Write(new byte[SharpLinkProtocolOptions.MinMaxFramePayloadBytes + 1]);
        }

        var exception = CaptureSharpLinkException(() => session.SendPacket(writer));

        Ensure(exception.Code == SharpLinkErrorCode.ResourceExhausted,
            "oversized original payload should fail locally");
        Ensure(provider.CompressCount == 0, "provider must not receive oversized original payload");
    }

    [Test]
    public async Task CompressionFailureShouldReturnAcquiredStreamCredit()
    {
        var provider = new ThrowIfCompressedProvider();
        await using var session = CreateSession(provider, enableFlowControl: true);

        var exception = await CaptureSharpLinkExceptionAsync(
            session.SendTestStreamChunkAsync(
                requestId: 1,
                streamId: 1,
                Enumerable.Repeat((byte)0x2a, 2048).ToArray()).AsTask());
        Ensure(exception.Code == SharpLinkErrorCode.Internal,
            "provider failure should remain a call-level Internal error");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await session.AcquireStreamSendCreditAsync(2, 1, 4096, timeout.Token);
    }

    private static RpcSession CreateSession(
        ISharpLinkCompressionProvider provider,
        bool enableCompression = true,
        bool enableFlowControl = false,
        int? maxFramePayloadBytes = null)
    {
        var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Compression.Providers.Add(provider))
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "compression-frame-test",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context),
            completeHandshake: false);
        var capabilities =
            (enableCompression ? ProtocolV2Capabilities.Compression : ProtocolV2Capabilities.None) |
            (enableFlowControl ? ProtocolV2Capabilities.FlowControl : ProtocolV2Capabilities.None);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            capabilities,
            maxFramePayloadBytes,
            streamReceiveWindowBytes: enableFlowControl ? 4096 : null,
            connectionReceiveWindowBytes: enableFlowControl ? 4096 : null,
            compressionBinding: enableCompression ? context.Compression.ProviderBindings[0] : null);
        return session;
    }

    private static byte[] Compress(
        ISharpLinkCompressionProvider provider,
        byte[] source)
    {
        using var writer = new PooledByteBufferWriter(source.Length);
        provider.TryCompress(new ReadOnlySequence<byte>(source), writer, source.Length);
        return writer.WrittenMemory.ToArray();
    }

    private static void WriteOriginalLength(IBufferWriter<byte> writer, int length)
    {
        var span = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(span, checked((uint)length));
        writer.Advance(sizeof(uint));
    }

    private static SharpLinkException CaptureSharpLinkException(Action action)
    {
        try
        {
            action();
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected SharpLinkException.");
    }

    private static async Task<SharpLinkException> CaptureSharpLinkExceptionAsync(Task action)
    {
        try
        {
            await action;
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected SharpLinkException.");
    }

    private static ReadOnlySequence<byte> CreateSegmented(byte[] bytes, int segmentSize)
    {
        Segment? first = null;
        Segment? last = null;
        for (var offset = 0; offset < bytes.Length; offset += segmentSize)
        {
            var segment = new Segment(bytes.AsMemory(offset, Math.Min(segmentSize, bytes.Length - offset)));
            if (first is null)
                first = segment;
            else
                last!.SetNext(segment);
            last = segment;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException($"Compression frame assertion failed: {scenario}.");
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal void SetNext(Segment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }


    // Observe the real pipe operations without consuming or wrapping their ValueTasks.
    // Snapshot fields are concurrent observations, not an atomic scheduler-state proof.
    private sealed class ObservedPipeWriter(PipeWriter inner) : PipeWriter
    {
        private readonly ConcurrentQueue<string> _events = new();
        private int _advancedBytes;
        private int _flushCalls;
        private int _completedFlushValueTasks;

        internal void Record(string phase) => _events.Enqueue(
            $"zero-body phase={phase} timestamp={Stopwatch.GetTimestamp()} thread={Environment.CurrentManagedThreadId}");

        internal void Snapshot(string phase, RpcSession session, ZeroBodyCompressionProvider provider, Task? flush)
        {
            ThreadPool.GetAvailableThreads(out var workers, out var io);
            Record($"{phase} compression={provider.CompressCount} advanced={Volatile.Read(ref _advancedBytes)} " +
                $"flushCalls={Volatile.Read(ref _flushCalls)} completedFlushValueTasks={Volatile.Read(ref _completedFlushValueTasks)} " +
                $"marker={flush?.Status.ToString() ?? "not-created"} queuedBytes={session.QueuedSendBytes} " +
                $"idleWait={session.HasPendingSendPumpIdleWait} connected={session.IsConnected} " +
                $"availableWorkers={workers} availableIo={io} pendingWork={ThreadPool.PendingWorkItemCount} " +
                $"threadCount={ThreadPool.ThreadCount} completedWork={ThreadPool.CompletedWorkItemCount}");
        }

        internal void Dump()
        {
            while (_events.TryDequeue(out var entry))
                Console.WriteLine(entry);
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            Record("get-memory-entered");
            var memory = inner.GetMemory(sizeHint);
            Record("get-memory-returned");
            return memory;
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            Record("get-span-entered");
            var span = inner.GetSpan(sizeHint);
            Record("get-span-returned");
            return span;
        }

        public override void Advance(int bytes)
        {
            Record($"advance-entered:{bytes}");
            inner.Advance(bytes);
            Interlocked.Add(ref _advancedBytes, bytes);
            Record($"advance-returned:{bytes}");
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _flushCalls);
            Record("pipe-flush-entered");
            var result = inner.FlushAsync(cancellationToken);
            if (result.IsCompletedSuccessfully)
                Interlocked.Increment(ref _completedFlushValueTasks);
            Record($"pipe-flush-returned:completed={result.IsCompleted}:success={result.IsCompletedSuccessfully}");
            return result;
        }

        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
        public override long UnflushedBytes => inner.UnflushedBytes;
    }

    private sealed class ZeroBodyCompressionProvider : ISharpLinkCompressionProvider
    {
        private int _compressCount;
        internal int CompressCount => Volatile.Read(ref _compressCount);
        public string WireProfile => "test.zero-body/v1";

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _compressCount);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!input.IsEmpty)
                throw new InvalidDataException("Zero-body test profile forbids representation bytes.");
            var remaining = maxOutputBytes;
            while (remaining != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var span = output.GetSpan(remaining);
                var count = Math.Min(span.Length, remaining);
                span[..count].Clear();
                output.Advance(count);
                remaining -= count;
            }
        }
    }

    private sealed class ThrowIfCompressedProvider : ISharpLinkCompressionProvider
    {
        internal int CompressCount { get; private set; }
        public string WireProfile => "test-oversized";

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            CompressCount++;
            throw new InvalidOperationException("Oversized payload reached the provider.");
        }

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
