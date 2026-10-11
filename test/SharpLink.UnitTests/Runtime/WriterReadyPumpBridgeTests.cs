using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

// Integration with the real private SendPump, not a reimplementation of its
// selection or settlement algorithm. Reflection is confined to this fixture.
public sealed class WriterReadyPumpBridgeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ReadyBufferMustRemainOwnedUntilFlushCompletes()
    {
        await using var pump = new PumpFixture(holdFirstFlush: true);
        var packet = new Packet(9);
        var source = new Source(packet);
        pump.Attach(source);
        await pump.Output.FirstFlushEntered.Task.WaitAsync(Limit);
        Ensure(packet.Returns == 0 && source.Releases == 0, "flush still owns frame");
        Ensure(pump.QueuedBytes == packet.WrittenCount, "queue reservation remains pinned");
        pump.Output.ReleaseFirstFlush();
        await source.Released.Task.WaitAsync(Limit);
        Ensure(packet.Returns == 1 && source.Releases == 1, "exactly one buffer/release");
        Ensure(pump.QueuedBytes == 0, "queue reservation returned");
        Ensure(pump.Output.Markers.SequenceEqual(new byte[] { 9 }), "exact bytes written");
    }

    [Test]
    public async Task StopCannotReturnAFrameFromABlockedFlush()
    {
        await using var pump = new PumpFixture(holdFirstFlush: true);
        var packet = new Packet(9);
        var source = new Source(packet);
        pump.Attach(source);
        await pump.Output.FirstFlushEntered.Task.WaitAsync(Limit);
        pump.Stop();
        Ensure(packet.Returns == 0 && source.Stops == 0, "stop does not steal writer ownership");
        pump.Output.ReleaseFirstFlush();
        await pump.Join().WaitAsync(Limit);
        Ensure(packet.Returns == 1 && source.Stops == 1, "writer settles before source stop");
        Ensure(source.ReleasesAtStop == 1, "stop follows admitted-frame release");
    }

    [Test]
    public async Task FailedFlushMustReleaseFrameAndStopSource()
    {
        await using var pump = new PumpFixture(holdFirstFlush: true);
        var packet = new Packet(9);
        var source = new Source(packet);
        pump.Attach(source);
        await pump.Output.FirstFlushEntered.Task.WaitAsync(Limit);
        pump.Output.ReleaseFirstFlush(new IOException("injected output fault"));
        await source.Stopped.Task.WaitAsync(Limit);
        await pump.Join().WaitAsync(Limit);
        Ensure(packet.Returns == 1 && source.Releases == 1, "failure must settle once");
        Ensure(source.ReleaseError is not null && pump.QueuedBytes == 0, "failed release carries error");
    }

    [Test]
    public async Task LateAttachmentMustNotStopSourceOnAttachingThread()
    {
        await using var pump = new PumpFixture();
        pump.Stop();
        await pump.Join().WaitAsync(Limit);
        var source = new Source();
        try { pump.Attach(source); throw new Exception("late attach was accepted"); }
        catch (SharpLinkException) { }
        Ensure(source.Stops == 0, "failed attachment did not transfer source ownership");
    }

    [Test]
    public async Task AttachmentRacingStopHasOneClearOwner()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            await using var pump = new PumpFixture();
            var source = new Source();
            using var start = new ManualResetEventSlim();
            var attach = Task.Run(() =>
            {
                start.Wait();
                try { pump.Attach(source); return true; }
                catch (SharpLinkException) { return false; }
            });
            var stop = Task.Run(() => { start.Wait(); pump.Stop(); });
            start.Set();
            await stop.WaitAsync(Limit);
            var attached = await attach.WaitAsync(Limit);
            await pump.Join().WaitAsync(Limit);
            Ensure(source.Stops == (attached ? 1 : 0), "attachment/shutdown ownership mismatch");
        }
    }

    [Test]
    public async Task ContinuouslyQueuedOrdinaryFramesMustNotStarveReadyFrame()
    {
        await using var pump = new PumpFixture(holdFirstFlush: true);
        pump.EnqueueOrdinary(new Packet(1));
        await pump.Output.FirstFlushEntered.Task.WaitAsync(Limit);
        for (var index = 0; index < 128; index++) pump.EnqueueOrdinary(new Packet(1));
        var source = new Source(new Packet(9));
        pump.Attach(source);
        pump.Output.ReleaseFirstFlush();
        await source.Released.Task.WaitAsync(Limit);
        var markers = pump.Output.Markers.ToArray();
        var position = Array.IndexOf(markers, (byte)9);
        Ensure(position >= 1 && position <= 17, "ready frame exceeded ordinary quantum");
    }

    [Test]
    public async Task FalseTakeAfterReservationMustNotLeakQueueBytes()
    {
        await using var pump = new PumpFixture();
        var packet = new Packet(9);
        var source = new Source(packet) { Fault = SourceFault.ReserveThenFalse };
        pump.Attach(source);
        await source.Stopped.Task.WaitAsync(Limit);
        await pump.Join().WaitAsync(Limit);
        Ensure(pump.QueuedBytes == 0, "false take leaked its reservation");
        Ensure(packet.Returns == 1 && source.Releases == 0, "source retained prepared ownership");
    }

    [Test]
    public async Task ThrowAfterReservationMustNotLeakQueueBytes()
    {
        await using var pump = new PumpFixture();
        var packet = new Packet(9);
        var source = new Source(packet) { Fault = SourceFault.ReserveThenThrow };
        pump.Attach(source);
        await source.Stopped.Task.WaitAsync(Limit);
        await pump.Join().WaitAsync(Limit);
        Ensure(pump.QueuedBytes == 0 && packet.Returns == 1, "throw leaked reservation or packet");
    }

    [Test]
    public async Task DuplicateReservationMustBeRejectedAndRolledBack()
    {
        await using var pump = new PumpFixture();
        var packet = new Packet(9);
        var source = new Source(packet) { Fault = SourceFault.ReserveTwice };
        pump.Attach(source);
        await source.Stopped.Task.WaitAsync(Limit);
        await pump.Join().WaitAsync(Limit);
        Ensure(pump.QueuedBytes == 0 && packet.Returns == 1, "duplicate reservation rollback failed");
    }

    [Test]
    public async Task LengthMismatchMustReturnTransferredPacketExactlyOnce()
    {
        await using var pump = new PumpFixture();
        var packet = new Packet(9);
        var source = new Source(packet) { Fault = SourceFault.WrongLength };
        pump.Attach(source);
        await source.Stopped.Task.WaitAsync(Limit);
        await pump.Join().WaitAsync(Limit);
        Ensure(pump.QueuedBytes == 0 && packet.Returns == 1, "transferred invalid packet leaked");
        Ensure(source.Releases == 1 && source.ReleaseError is not null, "source lease not failed");
    }

    [Test]
    public async Task ReleaseFailureMustNotDoubleReleaseTheBatchOrSkipSourceStop()
    {
        await using var pump = new PumpFixture(lowLatency: false);
        var first = new Packet(7) { ThrowOnReturn = true };
        var second = new Packet(8);
        var source = new Source(first, second);
        pump.Attach(source);
        await source.Stopped.Task.WaitAsync(Limit);
        try { await pump.Join().WaitAsync(Limit); }
        catch (AggregateException) { }
        Ensure(first.Returns == 1 && second.Returns == 1, "batch was released twice or truncated");
        Ensure(source.Releases == 2 && source.Stops == 1, "final source settlement skipped");
        Ensure(pump.QueuedBytes == 0, "failed release corrupted queue accounting");
    }

    [Test]
    public async Task ContinuouslyReadyFramesMustNotStarveOrdinaryFrame()
    {
        await using var pump = new PumpFixture(holdFirstFlush: true);
        var source = new Source(Enumerable.Range(0, 128).Select(_ => new Packet(9)).ToArray());
        pump.Attach(source);
        await pump.Output.FirstFlushEntered.Task.WaitAsync(Limit);
        pump.EnqueueOrdinary(new Packet(7));
        pump.Output.ReleaseFirstFlush();
        await source.Released.Task.WaitAsync(Limit);
        var position = Array.IndexOf(pump.Output.Markers.ToArray(), (byte)7);
        Ensure(position >= 1 && position <= 16, "ordinary frame exceeded ready quantum");
    }

    [Test]
    public async Task AttachedSourceMustPreserveExplicitTimedBatchPolicy()
    {
        var clock = new ManualClock();
        await using var pump = new PumpFixture(clock: clock,
            flushOptions: new RpcSessionFlushOptions(64 * 1024, TimeSpan.FromSeconds(1)));
        var packet = new Packet(9);
        var source = new Source(packet);
        pump.Attach(source);
        await clock.TimerArmed.Task.WaitAsync(Limit);
        Ensure(!pump.Output.FirstFlushEntered.Task.IsCompleted, "source bypassed timed batching");
        Ensure(packet.Returns == 0, "pending batch lost its buffer");
        clock.Advance(TimeSpan.FromSeconds(1));
        await source.Released.Task.WaitAsync(Limit);
        Ensure(packet.Returns == 1 && pump.QueuedBytes == 0, "timed batch did not settle");
    }

    [Test]
    public async Task ForceFlushInReadyLaneMustPreservePrecedingDataOrder()
    {
        var clock = new ManualClock();
        await using var pump = new PumpFixture(clock: clock,
            flushOptions: new RpcSessionFlushOptions(64 * 1024, TimeSpan.FromSeconds(1)));
        var source = new Source(new Packet(1), new Packet(9)) { ForceFlushLast = true };
        pump.Attach(source);
        await source.Released.Task.WaitAsync(Limit);
        Ensure(pump.Output.Markers.SequenceEqual(new byte[] { 1, 9 }), "ready lane reordered data/terminal marker");
        Ensure(!clock.TimerArmed.Task.IsCompleted, "force flush waited for batch deadline");
        // This verifies the bridge's lane only. It does not mean the generated
        // RPC producer already publishes StreamComplete through this source.
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private enum SourceFault { None, ReserveThenFalse, ReserveThenThrow, ReserveTwice, WrongLength }

    private sealed class Source : IWriterReadySource, IWriterReadyCompletion
    {
        private readonly Queue<Packet> _frames;
        private readonly int _initialCount;
        internal Source(params Packet[] frames) { _frames = new(frames); _initialCount = frames.Length; }
        internal SourceFault Fault;
        internal bool ForceFlushLast;
        internal int Releases, Stops, ReleasesAtStop;
        internal Exception? ReleaseError;
        internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasWork => _frames.Count != 0;
        public bool TryTake(IWriterReadyAdmission admission, out WriterReadyFrame frame)
        {
            frame = default;
            if (!_frames.TryPeek(out var packet)) return false;
            var reserve = packet.WrittenCount + (Fault == SourceFault.WrongLength ? 1 : 0);
            if (!admission.TryReserve(reserve)) return false;
            if (Fault == SourceFault.ReserveThenFalse) return false;
            if (Fault == SourceFault.ReserveThenThrow) throw new IOException("injected source fault");
            if (Fault == SourceFault.ReserveTwice) admission.TryReserve(reserve);
            _frames.Dequeue();
            var forceFlush = ForceFlushLast && _frames.Count == 0;
            frame = new WriterReadyFrame(packet, forceFlush ? 0 : 1, this, forceFlush);
            return true;
        }
        public void Complete(int creditBytes, Exception? error)
        {
            ReleaseError ??= error;
            if (++Releases == _initialCount) Released.TrySetResult();
        }
        void IWriterReadySource.Stopped(Exception error)
        {
            ReleasesAtStop = Releases;
            Stops++;
            while (_frames.TryDequeue(out var packet)) packet.Dispose();
            Stopped.TrySetResult();
        }
    }

    private sealed class Packet : IRpcByteBufferWriter
    {
        private readonly byte[] _bytes = new byte[24];
        private int _written = 24;
        internal Packet(byte marker) => _bytes[0] = marker;
        internal int Returns;
        internal bool ThrowOnReturn;
        public int WrittenCount => _written;
        public int Capacity => _bytes.Length;
        public ReadOnlyMemory<byte> WrittenMemory => _bytes.AsMemory(0, _written);
        public Span<byte> WrittenSpan => _bytes.AsSpan(0, _written);
        public void Clear() => _written = 0;
        public void Advance(int count) => _written = checked(_written + count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _bytes.AsMemory(_written);
        public Span<byte> GetSpan(int sizeHint = 0) => _bytes.AsSpan(_written);
        public void Dispose()
        {
            if (Interlocked.Increment(ref Returns) != 1) throw new Exception("packet returned twice");
            if (ThrowOnReturn) throw new IOException("injected pool-return fault");
        }
    }

    private sealed class ControlledOutput : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly bool _holdFirst;
        private readonly TaskCompletionSource<FlushResult> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _flushes;
        internal ControlledOutput(bool holdFirst) => _holdFirst = holdFirst;
        internal readonly TaskCompletionSource FirstFlushEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<byte> Markers = new();
        internal void ReleaseFirstFlush(Exception? error = null)
        {
            if (error is null) _first.TrySetResult(new FlushResult(false, false));
            else _first.TrySetException(error);
        }
        public override void Advance(int bytes)
        {
            _buffer.Advance(bytes);
            // All fixture packets are exactly 24 bytes.
            for (var offset = 0; offset < _buffer.WrittenCount; offset += 24)
                Markers.Enqueue(_buffer.WrittenSpan[offset]);
            _buffer.Clear();
        }
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _flushes) == 1)
            {
                FirstFlushEntered.TrySetResult();
                if (_holdFirst) return new(_first.Task); // Deliberately holds through Stop.
            }
            return new(new FlushResult(false, false));
        }
        public override void CancelPendingFlush() => _first.TrySetResult(new FlushResult(true, false));
        public override void Complete(Exception? exception = null) { }
    }

    // Deterministic clock: no sleep-based negative assertion in the policy tests.
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private long _ticks = 1;
        internal readonly TaskCompletionSource TimerArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        internal void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
            List<ManualTimer> fire = new();
            lock (_gate)
            {
                _ticks = checked(_ticks + duration.Ticks);
                foreach (var timer in _timers)
                {
                    if (timer.Disposed || timer.Due > _ticks) continue;
                    timer.Due = timer.Period > 0 ? checked(_ticks + timer.Period) : long.MaxValue;
                    fire.Add(timer);
                }
            }
            foreach (var timer in fire) timer.Callback(timer.State);
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal long Due = long.MaxValue, Period;
            internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(clock._ticks + dueTime.Ticks);
                    Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                    if (Due != long.MaxValue) clock.TimerArmed.TrySetResult();
                    return true;
                }
            }
            public void Dispose() { lock (clock._gate) { Disposed = true; Due = long.MaxValue; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class PumpFixture : IAsyncDisposable
    {
        private static readonly Type PumpType = typeof(RpcSession).GetNestedType("SendPump", BindingFlags.NonPublic)!;
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly object _pump;
        internal ControlledOutput Output { get; }
        internal PumpFixture(bool holdFirstFlush = false, bool lowLatency = true,
            TimeProvider? clock = null, RpcSessionFlushOptions? flushOptions = null)
        {
            Output = new(holdFirstFlush);
            var policy = RpcSessionFlushPolicyState.Create(flushOptions,
                lowLatency ? SharpLinkPerformanceProfile.LowLatency : SharpLinkPerformanceProfile.Throughput);
            _pump = Activator.CreateInstance(PumpType, Flags, binder: null,
                args: [Output, policy, 1024 * 1024, clock ?? TimeProvider.System, CancellationToken.None,
                    (Action<IRpcByteBufferWriter>)(packet => packet.Dispose()), (Action<Exception>)(_ => { })],
                culture: null)!;
        }
        private object? Call(string name, params object?[] args)
        {
            var method = PumpType.GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length);
            try { return method.Invoke(_pump, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
        internal void Attach(Source source) => Call("AttachWriterReadySource", source);
        internal void Stop() => Call("Stop");
        internal Task Join() => ((ValueTask)Call("WaitForStopAsync")!).AsTask();
        internal long QueuedBytes => (long)PumpType.GetProperty("QueuedBytes", Flags)!.GetValue(_pump)!;
        internal void EnqueueOrdinary(Packet packet)
        {
            var frame = new OwnedFrame(packet, false, null, false);
            var result = Call("TryEnqueue", frame);
            Ensure(result?.ToString() == "Accepted", "ordinary fixture frame was not accepted");
        }
        public async ValueTask DisposeAsync()
        {
            Stop();
            Output.ReleaseFirstFlush();
            try { await Join().WaitAsync(Limit); }
            catch (AggregateException) { /* Fault cases assert settlement before fixture disposal. */ }
        }
    }
}
