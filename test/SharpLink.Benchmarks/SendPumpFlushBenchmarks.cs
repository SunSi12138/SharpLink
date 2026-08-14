using System;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
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
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

[Config(typeof(SendPumpFlushBenchmarkConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
[ThreadingDiagnoser]
[OperationsPerSecond]
public class SendPumpFlushBenchmarks
{
    [Params(
        SharpLinkPerformanceProfile.LowLatency,
        SharpLinkPerformanceProfile.Balanced,
        SharpLinkPerformanceProfile.Throughput)]
    public SharpLinkPerformanceProfile Profile { get; set; }

    [Params(1, 4)]
    public int Concurrency { get; set; }

    [Params(false, true)]
    public bool AsyncFlush { get; set; }

    private SharpLinkRuntimeContext _context = null!;
    private RpcSession _session = null!;
    private Pipe _input = null!;
    private ControlledFlushPipeWriter _output = null!;
    private Task[] _workers = Array.Empty<Task>();

    [GlobalSetup]
    public void Setup()
    {
        _context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.PerformanceProfile = Profile)
            .Build(includeGeneratedAssemblyCatalog: false);
        _input = new Pipe();
        _output = new ControlledFlushPipeWriter(AsyncFlush);
        _session = new RpcSession(
            new BenchmarkTransportConnection(
                $"issue156-send-pump-{Profile}-{Concurrency}-{AsyncFlush}",
                _input.Reader,
                _output),
            new RpcSessionCreationOptions(RpcSessionRole.Client, _context));
        if (!_session.TryCompleteHandshake(new NegotiatedSessionOptions(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                _context.Protocol.MaxFramePayloadBytes,
                _context.FlowControl.StreamReceiveWindowBytes,
                _context.FlowControl.ConnectionReceiveWindowBytes)))
        {
            throw new InvalidOperationException("Issue 156 benchmark session handshake completion failed.");
        }

        if (Concurrency > 1)
            _workers = new Task[Concurrency];
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _session.DisposeAsync().ConfigureAwait(false);
        await _input.Writer.CompleteAsync().ConfigureAwait(false);
        await _output.CompleteAsync().ConfigureAwait(false);
        _context.Dispose();
    }

    [Benchmark]
    public async ValueTask SendPacketAndFlush()
    {
        if (Concurrency == 1)
        {
            await SendPacketAndFlushCoreAsync().ConfigureAwait(false);
            return;
        }

        for (var index = 0; index < _workers.Length; index++)
            _workers[index] = SendPacketAndFlushCoreAsync().AsTask();
        await Task.WhenAll(_workers).ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask FlushSendQueue()
    {
        if (Concurrency == 1)
        {
            await _session.FlushSendQueueAsync().ConfigureAwait(false);
            return;
        }

        for (var index = 0; index < _workers.Length; index++)
            _workers[index] = _session.FlushSendQueueAsync().AsTask();
        await Task.WhenAll(_workers).ConfigureAwait(false);
    }

    private async ValueTask SendPacketAndFlushCoreAsync()
    {
        var writer = _context.Buffers.Rent();
        writer.WritePacket(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, 1);
        await _session.SendPacketAndFlushAsync(writer).ConfigureAwait(false);
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

    private sealed class ControlledFlushPipeWriter : PipeWriter, IValueTaskSource<FlushResult>
    {
        private readonly bool _asyncFlush;
        private byte[] _buffer = new byte[4096];
        private int _written;
        private ManualResetValueTaskSourceCore<FlushResult> _core;

        public ControlledFlushPipeWriter(bool asyncFlush)
        {
            _asyncFlush = asyncFlush;
            _core.RunContinuationsAsynchronously = true;
        }

        public override void Advance(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            if (_written > _buffer.Length - bytes)
                throw new InvalidOperationException("The issue 156 benchmark writer advanced beyond its acquired buffer.");
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
            if (!_asyncFlush)
                return ValueTask.FromResult(new FlushResult(isCanceled: false, isCompleted: false));

            _core.Reset();
            ThreadPool.UnsafeQueueUserWorkItem(
                static state => ((ControlledFlushPipeWriter)state!).CompleteFlush(),
                this,
                preferLocal: false);
            return new ValueTask<FlushResult>(this, _core.Version);
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

        public FlushResult GetResult(short token) => _core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);

        private void CompleteFlush()
            => _core.SetResult(new FlushResult(isCanceled: false, isCompleted: false));

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

public sealed class SendPumpFlushBenchmarkConfig : ManualConfig
{
    public SendPumpFlushBenchmarkConfig()
    {
        AddColumn(StatisticColumn.P50, SendPumpFlushP95Column.Instance, SendPumpFlushP99Column.Instance);
    }
}

public sealed class SendPumpFlushP95Column : IColumn
{
    public static SendPumpFlushP95Column Instance { get; } = new();

    public string Id => nameof(SendPumpFlushP95Column);
    public string ColumnName => "P95";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Statistics;
    public int PriorityInCategory => 3;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Time;
    public string Legend => "95th percentile of the BenchmarkDotNet workload measurements.";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        => GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        => PercentileColumn.GetValue(summary, benchmarkCase, style, 0.95);

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    public bool IsAvailable(Summary summary) => true;
}

public sealed class SendPumpFlushP99Column : IColumn
{
    public static SendPumpFlushP99Column Instance { get; } = new();

    public string Id => nameof(SendPumpFlushP99Column);
    public string ColumnName => "P99";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Statistics;
    public int PriorityInCategory => 4;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Time;
    public string Legend => "99th percentile of the BenchmarkDotNet workload measurements.";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        => GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        => PercentileColumn.GetValue(summary, benchmarkCase, style, 0.99);

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    public bool IsAvailable(Summary summary) => true;
}

internal static class PercentileColumn
{
    internal static string GetValue(
        Summary summary,
        BenchmarkCase benchmarkCase,
        SummaryStyle style,
        double percentile)
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
        var position = percentile * (values.Length - 1);
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
}
