using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
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
    private readonly DateTimeOffset _deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    private SharpLinkRuntimeContext _context = null!;
    private PendingRequestTable _pending = null!;
    private byte[] _responsePayload = null!;
    private ReadOnlySequence<byte> _requestFrame;
    private ReadOnlySequence<byte> _metadataFrame;
    private ReadOnlySequence<byte> _segmentedMetadataFrame;
    private PooledByteBufferWriter _frameWriter = null!;
    [GlobalSetup]
    public void Setup()
    {
        _context = new SharpLinkRuntimeContextBuilder().Build();
        _pending = new PendingRequestTable(
            65_536,
            _context.Codecs,
            BenchmarkPendingCallOwner.Instance,
            TimeProvider.System);
        _responsePayload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(_responsePayload, 42);
        _requestFrame = new ReadOnlySequence<byte>(CreateRequestFrame(includeMetadata: false));
        var metadataBytes = CreateRequestFrame(includeMetadata: true);
        _metadataFrame = new ReadOnlySequence<byte>(metadataBytes);
        _segmentedMetadataFrame = CreateSegmented(metadataBytes, 1);
        _frameWriter = new PooledByteBufferWriter(64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _frameWriter.Dispose();
        _pending.Dispose();
        _context.Dispose();
    }

    [Benchmark]
    public int WriteRequestFrame()
    {
        _frameWriter.Clear();
        var token = ProtocolV2FrameWriter.BeginFrame(
            _frameWriter,
            ProtocolV2FrameType.Request,
            ProtocolV2FrameFlags.None,
            1);
        _frameWriter.Advance(ProtocolV2Constants.RequestPrefixBytes);
        ProtocolV2FrameWriter.EndFrame(_frameWriter, token);
        return _frameWriter.WrittenCount;
    }

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

    [Benchmark(Baseline = true)]
    public void CreatePushAndRestoreCallContext()
    {
        var callContext = new SharpLinkCallContextSnapshot("benchmark", authentication: null);
        using var scope = SharpLinkCallContext.Push(callContext);
        _ = SharpLinkCallContext.Current;
    }

    [Benchmark]
    public void CreateDeadlinePushAndRestoreCallContext()
    {
        var callContext = new SharpLinkCallContextSnapshot(
            "benchmark",
            authentication: null,
            _deadline);
        using var scope = SharpLinkCallContext.Push(callContext);
        _ = SharpLinkCallContext.Current;
    }

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
public class MetadataAllocationBenchmarks
{
    private ReadOnlySequence<byte> _payload;

    [GlobalSetup]
    public void Setup()
    {
        using var writer = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteMetadata(writer, new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "factory-a"),
            new KeyValuePair<string, string>("trace", "42")));
        _payload = new ReadOnlySequence<byte>(writer.WrittenMemory.ToArray());
    }

    [Benchmark]
    public SharpLinkMetadata ConstructTwoEntries()
        => new(
            new KeyValuePair<string, string>("tenant", "factory-a"),
            new KeyValuePair<string, string>("trace", "42"));

    [Benchmark]
    public SharpLinkMetadata DecodeTwoEntries()
        => ProtocolV2PayloadCodec.ReadMetadata(_payload);
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class FlowControlHotPathBenchmarks
{
    private StreamFlowController _flowController = null!;
    private long[] _requestIds = null!;

    [Params(1, 8, 32, 128, 512)]
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
[BenchmarkCategory("FlowControl", "Allocation")]
public class ReceiveFlowStateAllocationBenchmarks
{
    private StreamFlowController _flowController = null!;
    private long _requestId = 1;
    private ushort _streamId = 1;
    private int _encodedBytes = 32;

    [GlobalSetup]
    public void Setup()
    {
        _flowController = new StreamFlowController(
            streamWindow: 1024,
            connectionWindow: 1024,
            maxFramePayloadBytes: 4 * 1024 * 1024,
            maxConcurrentStreams: 1);
    }

    [Benchmark]
    public int ReceiveAndCompleteStream()
    {
        _flowController.AcceptReceived(_requestId, _streamId, _encodedBytes);
        _ = _flowController.RecordConsumed(_requestId, _streamId, _encodedBytes);
        return _flowController.FlushConsumed(_requestId, _streamId);
    }
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 15)]
public class CodecAndPreAdmissionHotPathBenchmarks
{
    private SharpLinkRuntimeContext _context = null!;
    private SharpLinkRuntimeContext _fallbackContext = null!;
    private SharpLinkRuntimeContext _generatedContext = null!;
    private StreamManager _streams = null!;
    private ReadOnlySequence<byte> _payload;

    [GlobalSetup]
    public void Setup()
    {
        _context = new SharpLinkRuntimeContextBuilder()
            .AddCodec(new BenchmarkValueCodec())
            .Build(includeGeneratedAssemblyCatalog: false);
        _fallbackContext = new SharpLinkRuntimeContextBuilder()
            .UseCodecResolver(static type => type == typeof(FallbackBenchmarkValue)
                ? new FallbackBenchmarkValueCodec()
                : null)
            .Build(includeGeneratedAssemblyCatalog: false);
        _ = _fallbackContext.Codecs.GetCodec<FallbackBenchmarkValue>();
        _generatedContext = new SharpLinkRuntimeContextBuilder()
            .Build([BenchmarkManifest.Instance]);
        _ = _generatedContext.Codecs.GetCodec<GeneratedBenchmarkValue>();
        _streams = new StreamManager();
        _streams.ReservePreAdmissionStreams(
            1,
            1,
            _context.Buffers,
            static _ => true,
            static _ => { },
            static () => throw new InvalidOperationException("Benchmark capacity exceeded."));
        _streams.Register(1, 1, new NoOpDispatcher());
        _payload = new ReadOnlySequence<byte>(new byte[] { 42 });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _streams.CompleteRequestStreams(1, exception: null);
        _generatedContext.Dispose();
        _fallbackContext.Dispose();
        _context.Dispose();
    }

    [Benchmark]
    public IRpcCodec<BenchmarkValue> CachedCodecLookup()
        => _context.Codecs.GetCodec<BenchmarkValue>();

    [Benchmark]
    public IRpcCodec<FallbackBenchmarkValue> CachedFallbackCodecLookup()
        => _fallbackContext.Codecs.GetCodec<FallbackBenchmarkValue>();

    [Benchmark]
    public IRpcCodec<GeneratedBenchmarkValue> CachedGeneratedCodecLookup()
        => _generatedContext.Codecs.GetCodec<GeneratedBenchmarkValue>();

    [Benchmark]
    public ValueTask AttachedPreAdmissionDispatch()
        => _streams.DispatchChunkAsync(1, 1, _payload);

    public sealed class BenchmarkValue;
    public sealed class FallbackBenchmarkValue;
    public sealed class GeneratedBenchmarkValue;

    private sealed class BenchmarkValueCodec : IRpcCodec<BenchmarkValue>
    {
        public void Serialize(in BenchmarkValue value, IBufferWriter<byte> buffer)
        {
        }

        public BenchmarkValue Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class FallbackBenchmarkValueCodec : IRpcCodec<FallbackBenchmarkValue>
    {
        public void Serialize(in FallbackBenchmarkValue value, IBufferWriter<byte> buffer) { }
        public FallbackBenchmarkValue Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class GeneratedBenchmarkValueCodec : IRpcCodec<GeneratedBenchmarkValue>
    {
        public void Serialize(in GeneratedBenchmarkValue value, IBufferWriter<byte> buffer) { }
        public GeneratedBenchmarkValue Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class BenchmarkCodecFactory : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(GeneratedBenchmarkValue);
        public string SchemaId => "benchmark-generated-v1";
        public string WireFormatId => "sharplink-native/v1";
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => new GeneratedBenchmarkValueCodec();
        public bool IsCompatibleCodec(IRpcCodec codec)
            => codec is IRpcCodec<GeneratedBenchmarkValue>;
    }

    private sealed class BenchmarkManifest : ISharpLinkGeneratedAssemblyManifest
    {
        internal static BenchmarkManifest Instance { get; } = new();
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "benchmark";
        public Assembly OwnerAssembly => typeof(BenchmarkManifest).Assembly;
        public string CompileTimeDescriptor => "benchmark";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [new BenchmarkCodecFactory()];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class NoOpDispatcher : IStreamDispatcher
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
    }
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class ServerCallCancellationStateBenchmarks
{
    private static readonly long SDeadlineOffset = 30L * Stopwatch.Frequency;
    private StripedLongMap<ServerCallCancellationState> _scheduledCalls = null!;
    private ServerCallDeadlineScheduler _scheduler = null!;
    private long _nextRequestId;

    [GlobalSetup]
    public void Setup()
    {
        _scheduledCalls = new StripedLongMap<ServerCallCancellationState>(
            new RuntimeConcurrencyOptions());
        _scheduler = new ServerCallDeadlineScheduler(
            _scheduledCalls,
            maxCalls: 1024,
            TimeProvider.System);
    }

    [GlobalCleanup]
    public void Cleanup() => _scheduler.Dispose();

    [Benchmark(Baseline = true)]
    public void NoDeadline()
    {
        var state = ServerCallCancellationState.Rent(
            1,
            default,
            TimeProvider.System,
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
            RpcDeadline.Create(
                DateTimeOffset.UtcNow.AddSeconds(30),
                Stopwatch.GetTimestamp() + SDeadlineOffset),
            TimeProvider.System,
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
            RpcDeadline.Create(
                DateTimeOffset.UtcNow.AddSeconds(30),
                Stopwatch.GetTimestamp() + SDeadlineOffset),
            TimeProvider.System,
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
            default,
            TimeProvider.System,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: true);
        state.TryCancel(ServerCallCancellationReason.RemoteCancel);
        state.Dispose();
    }

    [Benchmark]
    public void ScheduleDeadlineRegisterAndComplete()
    {
        var requestId = ++_nextRequestId;
        var state = ServerCallCancellationState.Rent(
            requestId,
            RpcDeadline.Create(DateTimeOffset.MaxValue, long.MaxValue),
            TimeProvider.System,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: false);
        _scheduledCalls.Set(requestId, state);
        _scheduler.Register(state);
        if (!_scheduledCalls.TryRemove(requestId, state))
            throw new InvalidOperationException("Scheduled benchmark call was not removed.");
        state.Dispose();
    }
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class RuntimeTimingHotPathBenchmarks
{
    private SharpLinkCircuitBreaker _breaker = null!;
    private SharpLinkEndpointCandidate _endpoint;
    private RpcMethodDescriptor _method;

    [GlobalSetup]
    public void Setup()
    {
        _breaker = new SharpLinkCircuitBreaker(new SharpLinkCircuitBreakerOptions().CloneValidated());
        _endpoint = new SharpLinkEndpointCandidate(
            new SharpLinkEndpoint
            {
                Id = "timing-benchmark",
                Address = new SharpLinkTcpAddress("127.0.0.1", 5001)
            },
            readyConnectionCount: 1,
            activeCallCount: 0,
            generation: 1);
        _method = new RpcMethodDescriptor(
            1,
            2,
            RpcMethodKind.Unary,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: false,
            MethodTimeout: null);
        _ = _breaker.TryAcquire(_endpoint, _method);
    }

    [Benchmark]
    public SharpLinkEndpointAdmissionDecision CircuitBreakerClosedTryAcquire()
        => _breaker.TryAcquire(_endpoint, _method);
}
