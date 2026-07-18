using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class RuntimeHotPathBenchmarks
{
    private readonly SharpLinkProtocolOptions _limits = new();
    private readonly SharpLinkCallContextSnapshot _callContext =
        new("benchmark", authentication: null);
    private PendingRequestTable _pending = null!;
    private byte[] _responsePayload = null!;
    private ReadOnlySequence<byte> _requestFrame;
    private ReadOnlySequence<byte> _metadataFrame;
    private ReadOnlySequence<byte> _segmentedMetadataFrame;
    [GlobalSetup]
    public void Setup()
    {
        var context = new SharpLinkRuntimeContextBuilder().Build();
        _pending = new PendingRequestTable(65_536, context.Codecs);
        _responsePayload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(_responsePayload, 42);
        _requestFrame = new ReadOnlySequence<byte>(CreateRequestFrame(includeMetadata: false));
        var metadataBytes = CreateRequestFrame(includeMetadata: true);
        _metadataFrame = new ReadOnlySequence<byte>(metadataBytes);
        _segmentedMetadataFrame = CreateSegmented(metadataBytes, 1);
    }

    [GlobalCleanup]
    public void Cleanup() => _pending.Dispose();

    [Benchmark]
    public async ValueTask<int> PendingRegisterAndComplete()
    {
        var operation = _pending.Rent<int>(out var requestId);
        var payload = new ReadOnlySequence<byte>(_responsePayload);
        _pending.Dispatch(requestId, ref payload);
        return await operation.AsValueTask().ConfigureAwait(false);
    }

    [Benchmark]
    public ProtocolV2FrameHeader ParseContiguousRequest()
        => Parse(_requestFrame);

    [Benchmark]
    public ProtocolV2FrameHeader ParseContiguousMetadataRequest()
        => Parse(_metadataFrame);

    [Benchmark]
    public ProtocolV2FrameHeader ParseSegmentedMetadataRequest()
        => Parse(_segmentedMetadataFrame);

    [Benchmark]
    public void PushAndRestoreCallContext()
    {
        using var scope = SharpLinkCallContext.Push(_callContext);
        _ = SharpLinkCallContext.Current;
    }

    private ProtocolV2FrameHeader Parse(ReadOnlySequence<byte> frame)
    {
        if (!ProtocolV2FrameParser.TryReadFrame(ref frame, _limits, out var header, out _))
            throw new InvalidOperationException("Benchmark frame was incomplete.");
        return header;
    }

    private static byte[] CreateRequestFrame(bool includeMetadata)
    {
        using var writer = new PooledByteBufferWriter();
        var flags = includeMetadata
            ? ProtocolV2FrameFlags.HasMetadata
            : ProtocolV2FrameFlags.None;
        var token = ProtocolV2FrameWriter.BeginFrame(writer, ProtocolV2FrameType.Request, flags, 1);
        var prefix = writer.GetSpan(ProtocolV2Constants.RequestPrefixBytes);
        BinaryPrimitives.WriteInt64LittleEndian(prefix, 11);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[8..], 22);
        writer.Advance(ProtocolV2Constants.RequestPrefixBytes);
        if (includeMetadata)
        {
            var metadata = new SharpLinkMetadata(
                new KeyValuePair<string, string>("trace-id", "0123456789abcdef"),
                new KeyValuePair<string, string>("tenant", "benchmark"));
            var length = ProtocolV2PayloadCodec.GetMetadataPayloadLength(metadata);
            ProtocolV2PayloadCodec.WriteVarUInt32(writer, checked((uint)length));
            ProtocolV2PayloadCodec.WriteMetadata(writer, metadata);
        }
        ProtocolV2FrameWriter.EndFrame(writer, token);
        return writer.WrittenMemory.ToArray();
    }

    private static ReadOnlySequence<byte> CreateSegmented(byte[] bytes, int segmentSize)
    {
        Segment? first = null;
        Segment? last = null;
        for (var offset = 0; offset < bytes.Length; offset += segmentSize)
        {
            var length = Math.Min(segmentSize, bytes.Length - offset);
            var segment = new Segment(bytes.AsMemory(offset, length));
            if (first is null)
                first = segment;
            else
                last!.SetNext(segment);
            last = segment;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment SetNext(Segment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
            return next;
        }
    }
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class FlowControlHotPathBenchmarks
{
    private StreamFlowController _flowController = null!;
    private long[] _requestIds = null!;

    [Params(1, 8, 32, 128)]
    public int FlowStreams { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _flowController = new StreamFlowController(
            streamWindow: 1024,
            connectionWindow: 1024 * Math.Max(FlowStreams, 1),
            maxFramePayloadBytes: 4 * 1024 * 1024,
            maxConcurrentStreams: Math.Max(FlowStreams, 1));
        _requestIds = new long[FlowStreams];
        for (var index = 0; index < _requestIds.Length; index++)
            _requestIds[index] = index + 1;
    }

    [Benchmark]
    public void CreditRoundTrip()
    {
        for (var index = 0; index < _requestIds.Length; index++)
        {
            var requestId = _requestIds[index];
            _flowController.AcquireSendCreditAsync(requestId, 1, 32, CancellationToken.None)
                .GetAwaiter().GetResult();
            _flowController.ApplyWindowUpdate(requestId, 1, 32);
        }
    }
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class ServerCallCancellationStateBenchmarks
{
    private static readonly long SDeadlineOffset = 30L * Stopwatch.Frequency;

    [Benchmark(Baseline = true)]
    public void NoDeadline()
    {
        var state = ServerCallCancellationState.Rent(
            1,
            null,
            0,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: false);
        state.Dispose();
    }

    [Benchmark]
    public void CooperativeDeadline()
    {
        var state = ServerCallCancellationState.Rent(
            2,
            DateTimeOffset.UtcNow.AddSeconds(30),
            Stopwatch.GetTimestamp() + SDeadlineOffset,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        state.Dispose();
    }

    [Benchmark]
    public void NonCooperativeDeadline()
    {
        var state = ServerCallCancellationState.Rent(
            3,
            DateTimeOffset.UtcNow.AddSeconds(30),
            Stopwatch.GetTimestamp() + SDeadlineOffset,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: false);
        state.Dispose();
    }

    [Benchmark]
    public void CancelAndDispose()
    {
        var state = ServerCallCancellationState.Rent(
            4,
            null,
            0,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        state.TryCancel(ServerCallCancellationReason.RemoteCancel);
        state.Dispose();
    }
}
