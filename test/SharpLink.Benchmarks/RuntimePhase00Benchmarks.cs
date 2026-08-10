using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;
using Perfolizer.Metrology;
using Pragmastat.Metrology;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

[Config(typeof(RuntimePhase00BenchmarkConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
[ThreadingDiagnoser]
[OperationsPerSecond]
public class RuntimePhase00Benchmarks
{
    private BenchmarkEnvironment _environment = null!;
    private SharpLinkRuntimeContext _context = null!;
    private PendingRequestTable _pending = null!;
    private StreamManager _streams = null!;
    private RpcSession _sendSession = null!;
    private Pipe _sendInput = null!;
    private byte[] _responsePayload = null!;
    private ReadOnlySequence<byte> _streamPayload;
    private long _streamRequestId;
    private int _firstActiveCalls;
    private int _firstReadyConnections;
    private int _secondActiveCalls;
    private int _secondReadyConnections;

    [GlobalSetup]
    public async Task Setup()
    {
        _environment = await BenchmarkEnvironment.CreateAsync().ConfigureAwait(false);
        _context = new SharpLinkRuntimeContextBuilder()
            .AddCodec(new BenchmarkValueCodec())
            .Build(includeGeneratedAssemblyCatalog: false);
        _pending = new PendingRequestTable(
            65_536,
            _context.Codecs,
            BenchmarkPendingCallOwner.Instance,
            TimeProvider.System);
        _streams = new StreamManager();
        _sendInput = new Pipe();
        _sendSession = new RpcSession(
            new BenchmarkTransportConnection(
                "phase00-send-pump",
                _sendInput.Reader,
                new DiscardingPipeWriter()),
            new RpcSessionCreationOptions(RpcSessionRole.Client, _context));
        if (!_sendSession.TryCompleteHandshake(new NegotiatedSessionOptions(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                _context.Protocol.MaxFramePayloadBytes,
                _context.FlowControl.StreamReceiveWindowBytes,
                _context.FlowControl.ConnectionReceiveWindowBytes)))
        {
            throw new InvalidOperationException("Benchmark session handshake completion failed.");
        }
        _responsePayload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(_responsePayload, 42);
        _streamPayload = new ReadOnlySequence<byte>(new byte[] { 42 });
        _firstActiveCalls = 3;
        _firstReadyConnections = 2;
        _secondActiveCalls = 4;
        _secondReadyConnections = 3;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _sendSession.DisposeAsync().ConfigureAwait(false);
        await _sendInput.Writer.CompleteAsync().ConfigureAwait(false);
        _pending.Dispose();
        _context.Dispose();
        await _environment.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<int> UnarySendAndComplete()
        => _environment.Rpc.AddAsync(10, 20);

    [Benchmark]
    public bool SessionIsConnected()
        => _sendSession.IsConnected;

    [Benchmark(OperationsPerInvoke = 1024)]
    public int NegotiatedSnapshotRead()
    {
        var checksum = 0;
        for (var index = 0; index < 1024; index++)
            checksum = unchecked(checksum + (_sendSession.NegotiatedOptions?.MaxFramePayloadBytes ?? 0));
        return checksum;
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
    public async ValueTask<int> StreamManagerDispatchAndComplete()
    {
        var requestId = Interlocked.Increment(ref _streamRequestId);
        _streams.Register(requestId, 1, NoOpDispatcher.Instance);
        await _streams.DispatchChunkAsync(requestId, 1, _streamPayload).ConfigureAwait(false);
        _streams.CompleteRequestStreams(requestId, exception: null);
        return _streams.ActiveStreamCount;
    }

    [Benchmark]
    public async ValueTask<long> SendPumpEnqueueAndFlush()
    {
        var writer = _sendSession.RuntimeContext.Buffers.Rent();
        writer.WritePacket(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, 1);
        await _sendSession.SendPacketAndFlushAsync(writer).ConfigureAwait(false);
        return _sendSession.QueuedSendBytes;
    }

    [Benchmark]
    public int PowerOfTwoChoicesCompare()
        => EndpointSelectionKernel.CompareNormalizedLoad(
            _firstActiveCalls,
            _firstReadyConnections,
            _secondActiveCalls,
            _secondReadyConnections);

    [Benchmark]
    public IRpcCodec<BenchmarkValue> CachedCodecResolve()
        => _context.Codecs.GetCodec<BenchmarkValue>();

    public sealed class BenchmarkValue;

    private sealed class BenchmarkValueCodec : IRpcCodec<BenchmarkValue>
    {
        public void Serialize(in BenchmarkValue value, IBufferWriter<byte> buffer)
        {
            _ = value;
            _ = buffer;
        }

        public BenchmarkValue Deserialize(in ReadOnlySequence<byte> buffer)
        {
            _ = buffer;
            return new BenchmarkValue();
        }
    }

    private sealed class NoOpDispatcher : IStreamDispatcher
    {
        internal static NoOpDispatcher Instance { get; } = new();

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
        }

        public void Complete(Exception? exception) => _ = exception;
    }

    private sealed class BenchmarkTransportConnection(
        string id,
        PipeReader input,
        PipeWriter output) : ITransportConnection
    {
        public string Id { get; } = id;
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;

        public async ValueTask DisposeAsync()
        {
            await Output.CompleteAsync().ConfigureAwait(false);
            await Input.CompleteAsync().ConfigureAwait(false);
        }
    }

    private sealed class DiscardingPipeWriter : PipeWriter
    {
        private byte[] _buffer = new byte[4096];
        private int _written;

        public override void Advance(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            if (_written > _buffer.Length - bytes)
                throw new InvalidOperationException("The benchmark writer advanced beyond its acquired buffer.");
            _written += bytes;
        }

        public override void CancelPendingFlush()
        {
        }

        public override void Complete(Exception? exception = null)
        {
            _ = exception;
            _written = 0;
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _written = 0;
            return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: false));
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            var required = checked(_written + Math.Max(sizeHint, 1));
            if (required <= _buffer.Length)
                return;
            Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
        }
    }
}

public sealed class RuntimePhase00BenchmarkConfig : ManualConfig
{
    public RuntimePhase00BenchmarkConfig()
    {
        AddColumn(StatisticColumn.P50, P99Column.Instance);
    }
}

public sealed class P99Column : IColumn
{
    public static P99Column Instance { get; } = new();

    public string Id => nameof(P99Column);
    public string ColumnName => "P99";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Statistics;
    public int PriorityInCategory => 2;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Time;
    public string Legend => "99th percentile of the BenchmarkDotNet workload measurements.";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        => GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var report = summary[benchmarkCase];
        if (report is null)
            return "NA";

        var values = report.GetResultRuns()
            .Select(static measurement => measurement.Nanoseconds / measurement.Operations)
            .ToArray();
        if (values.Length == 0)
            return "NA";
        Array.Sort(values);
        var position = 0.99 * (values.Length - 1);
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = Math.Min(lowerIndex + 1, values.Length - 1);
        var nanoseconds = values[lowerIndex] +
            ((values[upperIndex] - values[lowerIndex]) * (position - lowerIndex));
        return PerfolizerMeasurementFormatter.Instance.Format(
            TimeInterval.FromNanoseconds(nanoseconds).ToMeasurement(style.TimeUnit),
            "N2",
            style.CultureInfo,
            new UnitPresentation(style.PrintUnitsInContent, minUnitWidth: 0, gap: true));
    }

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public bool IsAvailable(Summary summary) => true;
}
