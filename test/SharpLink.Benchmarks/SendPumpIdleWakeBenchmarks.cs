using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Issue 157 allocation baseline for the send-pump idle/wake cycle:
/// pump parked on <c>WakeupSignal</c> → producer enqueue → pump wake → drain → pump wait again.
/// Each invocation is one complete force-flush cycle, so the waiter registration cost of the
/// normal (non-timed-batch) wait path is included once per invocation.
/// </summary>
[Config(typeof(SendPumpFlushBenchmarkConfig))]
[MemoryDiagnoser(displayGenColumns: true)]
[ThreadingDiagnoser]
[OperationsPerSecond]
public class SendPumpIdleWakeBenchmarks
{
    public enum IdleWakeScenario
    {
        LowLatency,
        Balanced,
        Throughput,
        CustomTimedBatch
    }

    [Params(
        IdleWakeScenario.LowLatency,
        IdleWakeScenario.Balanced,
        IdleWakeScenario.Throughput,
        IdleWakeScenario.CustomTimedBatch)]
    public IdleWakeScenario Scenario { get; set; }

    private SharpLinkRuntimeContext _context = null!;
    private RpcSession _session = null!;
    private Pipe _input = null!;
    private NullFlushPipeWriter _output = null!;
    private ManualBenchmarkClock? _clock;

    [GlobalSetup]
    public void Setup()
    {
        var profile = Scenario switch
        {
            IdleWakeScenario.LowLatency => SharpLinkPerformanceProfile.LowLatency,
            IdleWakeScenario.Throughput => SharpLinkPerformanceProfile.Throughput,
            _ => SharpLinkPerformanceProfile.Balanced
        };
        _clock = Scenario == IdleWakeScenario.CustomTimedBatch
            ? new ManualBenchmarkClock()
            : null;
        var builder = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.PerformanceProfile = profile);
        if (_clock is not null)
            builder.UseTimeProvider(_clock);
        _context = builder.Build(includeGeneratedAssemblyCatalog: false);
        _input = new Pipe();
        _output = new NullFlushPipeWriter();
        RpcSessionFlushOptions? flushOptions = Scenario == IdleWakeScenario.CustomTimedBatch
            ? new RpcSessionFlushOptions(1024 * 1024, TimeSpan.FromMilliseconds(1))
            : null;
        _session = new RpcSession(
            new BenchmarkTransportConnection($"issue157-idle-wake-{Scenario}", _input.Reader, _output),
            new RpcSessionCreationOptions(RpcSessionRole.Client, _context, flushOptions));
        if (!_session.TryCompleteHandshake(new NegotiatedSessionOptions(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                _context.Protocol.MaxFramePayloadBytes,
                _context.FlowControl.StreamReceiveWindowBytes,
                _context.FlowControl.ConnectionReceiveWindowBytes)))
        {
            throw new InvalidOperationException("Issue 157 benchmark session handshake completion failed.");
        }
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
    public async ValueTask<long> IdleWakeForceFlushCycle()
    {
        var writer = _context.Buffers.Rent();
        writer.WritePacket(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, 1);
        await _session.SendPacketAndFlushAsync(writer).ConfigureAwait(false);
        return _session.QueuedSendBytes;
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

    private sealed class NullFlushPipeWriter : PipeWriter
    {
        private byte[] _buffer = new byte[4096];
        private int _written;

        public override void Advance(int bytes)
        {
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
            return ValueTask.FromResult(new FlushResult(isCanceled: false, isCompleted: false));
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
            var required = checked(_written + Math.Max(sizeHint, 1));
            if (required <= _buffer.Length)
                return;
            Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
        }
    }
}

/// <summary>
/// Issue 157 TimedBatch deadline observation: a non-force-flush small frame parks the pump in
/// <c>WaitForMoreUntilDeadlineAsync</c> (one wake arm completed by producer or timer). The manual
/// clock advances deterministically until the batch drains. Kept separate so the normal-wait
/// measurements above stay clean.
/// </summary>
[Config(typeof(SendPumpFlushBenchmarkConfig))]
[MemoryDiagnoser(displayGenColumns: true)]
[ThreadingDiagnoser]
[OperationsPerSecond]
public class SendPumpTimedBatchDeadlineBenchmarks
{
    private SharpLinkRuntimeContext _context = null!;
    private RpcSession _session = null!;
    private Pipe _input = null!;
    private NullFlushPipeWriter _output = null!;
    private ManualBenchmarkClock _clock = null!;

    [GlobalSetup]
    public void Setup()
    {
        _clock = new ManualBenchmarkClock();
        _context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(_clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        _input = new Pipe();
        _output = new NullFlushPipeWriter();
        _session = new RpcSession(
            new BenchmarkTransportConnection("issue157-timed-batch-deadline", _input.Reader, _output),
            new RpcSessionCreationOptions(
                RpcSessionRole.Client,
                _context,
                new RpcSessionFlushOptions(1024 * 1024, TimeSpan.FromMilliseconds(1))));
        if (!_session.TryCompleteHandshake(new NegotiatedSessionOptions(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                _context.Protocol.MaxFramePayloadBytes,
                _context.FlowControl.StreamReceiveWindowBytes,
                _context.FlowControl.ConnectionReceiveWindowBytes)))
        {
            throw new InvalidOperationException("Issue 157 benchmark session handshake completion failed.");
        }
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
    public async ValueTask<long> TimedBatchDeadlineCycle()
    {
        var writer = _context.Buffers.Rent();
        writer.WritePacket(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, 1);
        await _session.SendPacketAsync(writer, waitForCapacity: true, forceFlush: false)
            .ConfigureAwait(false);

        var spin = new SpinWait();
        while (_session.QueuedSendBytes != 0)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            spin.SpinOnce();
        }
        return _session.QueuedSendBytes;
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

    private sealed class NullFlushPipeWriter : PipeWriter
    {
        private byte[] _buffer = new byte[4096];
        private int _written;

        public override void Advance(int bytes)
        {
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
            return ValueTask.FromResult(new FlushResult(isCanceled: false, isCompleted: false));
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
            var required = checked(_written + Math.Max(sizeHint, 1));
            if (required <= _buffer.Length)
                return;
            Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
        }
    }
}

/// <summary>Deterministic benchmark clock mirroring the unit-test ManualTimeProvider.</summary>
internal sealed class ManualBenchmarkClock : TimeProvider
{
    private static readonly DateTimeOffset DefaultStart =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private readonly System.Collections.Generic.List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow;
    private long _timestamp;

    public ManualBenchmarkClock()
    {
        _utcNow = DefaultStart;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _utcNow;
    }

    public override long GetTimestamp()
    {
        lock (_gate)
            return _timestamp;
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        long target;
        lock (_gate)
            target = SaturatingAdd(_timestamp, elapsed.Ticks);

        while (true)
        {
            TimerCallback callback;
            object? state;
            lock (_gate)
            {
                var nextTimer = FindNextTimer(target);
                if (nextTimer is null)
                {
                    MoveClock(target);
                    return;
                }

                MoveClock(nextTimer.NextTimestamp);
                nextTimer.PrepareNextTick();
                callback = nextTimer.Callback;
                state = nextTimer.State;
            }

            callback(state);
        }
    }

    private ManualTimer? FindNextTimer(long target)
    {
        ManualTimer? next = null;
        for (var index = 0; index < _timers.Count; index++)
        {
            var candidate = _timers[index];
            if (candidate.IsDisposed || candidate.NextTimestamp > target)
                continue;
            if (next is null || candidate.NextTimestamp < next.NextTimestamp)
                next = candidate;
        }
        return next;
    }

    private void MoveClock(long timestamp)
    {
        var delta = timestamp - _timestamp;
        _timestamp = timestamp;
        _utcNow = _utcNow.AddTicks(delta);
    }

    private bool ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        var dueTicks = ValidateDelay(dueTime, nameof(dueTime));
        var periodTicks = ValidateDelay(period, nameof(period));

        lock (_gate)
        {
            if (timer.IsDisposed)
                return false;
            if (!_timers.Contains(timer))
                _timers.Add(timer);

            timer.PeriodTicks = periodTicks <= 0 ? long.MaxValue : periodTicks;
            timer.NextTimestamp = dueTicks == long.MaxValue
                ? long.MaxValue
                : SaturatingAdd(_timestamp, dueTicks);
            return true;
        }
    }

    private void DisposeTimer(ManualTimer timer)
    {
        lock (_gate)
        {
            if (timer.IsDisposed)
                return;
            timer.IsDisposed = true;
            timer.NextTimestamp = long.MaxValue;
            _timers.Remove(timer);
        }
    }

    private static long ValidateDelay(TimeSpan value, string parameterName)
    {
        if (value == Timeout.InfiniteTimeSpan)
            return long.MaxValue;
        if (value < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(parameterName);
        return value.Ticks;
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed class ManualTimer(
        ManualBenchmarkClock owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        internal TimerCallback Callback { get; } = callback;
        internal object? State { get; } = state;
        internal long NextTimestamp { get; set; } = long.MaxValue;
        internal long PeriodTicks { get; set; } = long.MaxValue;
        internal bool IsDisposed { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
            => owner.ChangeTimer(this, dueTime, period);

        public void Dispose() => owner.DisposeTimer(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        internal void PrepareNextTick()
        {
            NextTimestamp = PeriodTicks == long.MaxValue
                ? long.MaxValue
                : SaturatingAdd(NextTimestamp, PeriodTicks);
        }
    }
}
