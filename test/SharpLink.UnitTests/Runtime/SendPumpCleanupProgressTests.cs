using System.Diagnostics.Metrics;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public sealed class SendPumpCleanupProgressTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ThrowingBatchReturnMustNotReleaseRerentedOwnerOrAbandonEitherQueue()
    {
        var scope = new AsyncLocal<bool> { Value = true };
        var fixture = new Fixture();
        try
        {
            var seed = fixture.Session.SendPacketAndFlushAsync(fixture.Normal(1)).AsTask();
            scope.Value = false;
            await fixture.Output.FirstEntered.Task.WaitAsync(Timeout);
            var reused = fixture.Progress();
            var batchFailure = new InvalidOperationException("batch return");
            var middle = fixture.Progress(batchFailure);
            var last = fixture.Progress();
            Exception? reuseFailure = null;
            var reuseCount = 0;
            using var listener = ListenForRelease(scope, () =>
            {
                if (reused.ReturnCount != 1 || Interlocked.Exchange(ref reuseCount, 1) != 0)
                    return;
                try
                {
                    // The first Dispose has returned before its negative byte metric.
                    // This exact object now owns a distinct, live queued lease.
                    reused.BeginLease();
                    WriteFrame(reused, ProtocolV2FrameType.Response, 91, 32);
                    fixture.Session.SendPacket(reused);
                }
                catch (Exception exception) { reuseFailure = exception; }
            });
            fixture.Session.SendPacket(reused);
            fixture.Session.SendPacket(middle);
            fixture.Session.SendPacket(last);
            fixture.Output.ReleaseFirst();
            await seed.WaitAsync(Timeout);
            await fixture.Output.SecondEntered.Task.WaitAsync(Timeout);

            var progressFailure = new ApplicationException("queued progress return");
            var normalFailure = new IOException("queued normal return");
            var queuedProgress = fixture.Progress(progressFailure);
            var queuedNormal = fixture.Normal(2, normalFailure);
            fixture.Session.SendPacket(queuedProgress);
            fixture.Session.SendPacket(queuedNormal);
            var marker = fixture.Session.FlushSendQueueAsync().AsTask();
            var waitingOwner = fixture.Normal(3, payloadBytes: 1100);
            var waiting = fixture.Session.SendPacketWithBackpressureAsync(waitingOwner).AsTask();
            var waitingDrain = fixture.Session.CaptureBackpressuredSendDrain();
            Ensure(!waiting.IsCompleted && !waitingDrain.IsCompleted,
                "the oversized producer must genuinely wait behind the held batch");

            fixture.Output.ReleaseSecond();
            var terminal = await fixture.Disconnected.Task.WaitAsync(Timeout);
            var cleanup = await CaptureAsync(fixture.Session.DisposeAsync().AsTask());
            var secondCleanup = await CaptureAsync(fixture.Session.DisposeAsync().AsTask());
            await waitingDrain.WaitAsync(Timeout);
            Ensure(await CaptureAsync(waiting) is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
                "the capacity-waiting producer must finish with closure before inspecting cleanup ownership");
            Ensure(cleanup is not null && Contains(terminal, batchFailure),
                "completed disposal must expose cleanup failure after the injected return fault stopped the pump");
            Ensure(ReferenceEquals(cleanup, secondCleanup), "single-flight disposal must preserve failure identity");
            Ensure(reuseCount == 1 && reuseFailure is null && reused.Lease == 2,
                "the controlled callback must acquire and enqueue the second lease before ownership is inspected");

            // This deliberately narrow witness is also used by the baseline overlay
            // check. Both disposal calls and the capacity drain have completed, and
            // watchdog timeouts cannot be captured as operation failures below.
            // Do not await the marker first: the old defect leaves it queued forever.
            if (reused.ReturnCount == 2 && reused.ReturnsForLease(1) == 1 &&
                reused.ReturnsForLease(2) == 1 && middle.ReturnCount == 2 && last.ReturnCount == 0 &&
                queuedProgress.ReturnCount == 0 && queuedNormal.ReturnCount == 0 &&
                waitingOwner.ReturnCount == 1 && !marker.IsCompleted && fixture.Session.QueuedSendBytes > 0)
            {
                throw new InvalidOperationException(
                    "SENDPUMP_STALE_SLOT_CAUSAL_V1: completed cleanup retried a retired batch slot and abandoned later batch and queued owners.");
            }

            Ensure(Contains(cleanup, batchFailure) && Contains(cleanup, progressFailure) &&
                Contains(cleanup, normalFailure), "every distinct owner-cleanup failure must remain observable");
            Ensure(reuseCount == 1 && reuseFailure is null && reused.Lease == 2 &&
                reused.ReturnsForLease(1) == 1 && reused.ReturnsForLease(2) == 1,
                "an old batch slot must never return a newly acquired lease");
            Ensure(middle.ReturnCount == 1 && last.ReturnCount == 1 &&
                queuedProgress.ReturnCount == 1 && queuedNormal.ReturnCount == 1 && waitingOwner.ReturnCount == 1,
                "a throwing return must not repeat or abandon any later physical owner");
            Ensure(reused.FirstReturnSequence < middle.FirstReturnSequence &&
                middle.FirstReturnSequence < last.FirstReturnSequence,
                "cleanup must preserve the batch's original FIFO return order");
            Ensure(ReferenceEquals(await CaptureAsync(marker), terminal),
                "a queued zero-byte marker must settle with the winning terminal exception");
            Ensure(fixture.Session.QueuedSendBytes == 0 && fixture.Output.FlushCount == 2,
                "all reservations must balance without replaying the failed-cleanup batch or queued frames");
        }
        finally { await fixture.CleanupAsync(); }
    }

    [Test]
    [Arguments("throw")]
    [Arguments("canceled")]
    [Arguments("completed")]
    public async Task TerminalCleanupMustDrainBothQueuesAndPreserveTransportCause(string outputFailure)
    {
        var fixture = new Fixture(outputFailure);
        try
        {
            var seed = fixture.Session.SendPacketAndFlushAsync(fixture.Normal(1)).AsTask();
            await fixture.Output.FirstEntered.Task.WaitAsync(Timeout);
            var firstFailure = new InvalidOperationException("first terminal batch owner");
            var first = fixture.Progress(firstFailure);
            var second = fixture.Progress();
            fixture.Session.SendPacket(first);
            fixture.Session.SendPacket(second);
            fixture.Output.ReleaseFirst();
            await seed.WaitAsync(Timeout);
            await fixture.Output.SecondEntered.Task.WaitAsync(Timeout);

            var progressFailure = new ApplicationException("terminal progress owner");
            var normalFailure = new IOException("terminal normal owner");
            var observerFailure = new NotSupportedException("request failure observer");
            var queuedProgress = fixture.Progress(progressFailure);
            var queuedNormal = fixture.Normal(42, normalFailure);
            var observer = new ThrowingFailureObserver(observerFailure);
            fixture.Session.SendPacket(queuedProgress);
            fixture.Session.SendPacket(queuedNormal, observer);
            var marker = fixture.Session.FlushSendQueueAsync().AsTask();
            fixture.Output.ReleaseSecond();

            var terminal = await fixture.Disconnected.Task.WaitAsync(Timeout);
            var cleanup = await CaptureAsync(fixture.Session.DisposeAsync().AsTask());
            Ensure(terminal is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
                "the original transport failure remains the session terminal reason");
            if (outputFailure == "throw")
                Ensure(Contains(terminal, fixture.Output.Failure), "the injected transport cause must not be masked");
            Ensure(ReferenceEquals(await CaptureAsync(marker), terminal) && observer.Count == 1 &&
                observer.RequestId == 42 && ReferenceEquals(observer.Failure, terminal),
                "flush and request observers must retain the causal transport exception");
            Ensure(Contains(cleanup, firstFailure) && Contains(cleanup, progressFailure) &&
                Contains(cleanup, normalFailure) && Contains(cleanup, observerFailure),
                "return and observer failures must be exposed without abandoning later cleanup");
            Ensure(first.ReturnCount == 1 && second.ReturnCount == 1 && queuedProgress.ReturnCount == 1 &&
                queuedNormal.ReturnCount == 1 && fixture.Session.QueuedSendBytes == 0,
                "even the first terminal return throwing must not prevent either queue from draining");
            Ensure(fixture.Output.FlushCount == 2 && fixture.Output.AdvanceCount == 2,
                "terminal cleanup must not write or flush buffered frames again");
        }
        finally { await fixture.CleanupAsync(); }
    }

    [Test]
    public async Task StopMustJoinBlockedThrowingReturnAndStillCompleteMarkersAndCapacityWaiters()
    {
        var fixture = new Fixture();
        try
        {
            var seed = fixture.Session.SendPacketAndFlushAsync(fixture.Normal(1)).AsTask();
            await fixture.Output.FirstEntered.Task.WaitAsync(Timeout);
            var returnFailure = new InvalidOperationException("blocked return failure");
            var held = fixture.Progress(returnFailure, blockReturn: true);
            var later = fixture.Progress();
            fixture.Session.SendPacket(held);
            fixture.Session.SendPacket(later);
            fixture.Output.ReleaseFirst();
            await seed.WaitAsync(Timeout);
            await fixture.Output.SecondEntered.Task.WaitAsync(Timeout);
            var marker = fixture.Session.FlushSendQueueAsync().AsTask();
            var waitingOwner = fixture.Normal(4, payloadBytes: 1100);
            var waiting = fixture.Session.SendPacketWithBackpressureAsync(waitingOwner).AsTask();
            var drain = fixture.Session.CaptureBackpressuredSendDrain();
            Ensure(!waiting.IsCompleted && !drain.IsCompleted, "the producer must be waiting before shutdown");

            fixture.Output.ReleaseSecond();
            await held.ReturnEntered.Task.WaitAsync(Timeout);
            var dispose = fixture.Session.DisposeAsync().AsTask();
            Ensure(!dispose.IsCompleted, "retiring a slot cannot certify completion while physical return is held");
            held.ReleaseReturn();
            Ensure(Contains(await CaptureAsync(dispose), returnFailure),
                "a cleanup fault after a normal stopping winner must not be swallowed");
            Ensure(await CaptureAsync(marker) is SharpLinkException && await CaptureAsync(waiting) is SharpLinkException,
                "zero-byte markers and capacity waiters must settle after shutdown");
            await drain.WaitAsync(Timeout);
            Ensure(held.ReturnCount == 1 && later.ReturnCount == 1 && waitingOwner.ReturnCount == 1 &&
                fixture.Session.QueuedSendBytes == 0, "shutdown must release each physical owner and reservation once");
        }
        finally { await fixture.CleanupAsync(); }
    }

    private static MeterListener ListenForRelease(AsyncLocal<bool> scope, Action callback)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name == "sharplink.send.queue.bytes")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, bytes, _, _) =>
        {
            if (scope.Value && bytes < 0) callback();
        });
        listener.Start();
        return listener;
    }

    [Test]
    public async Task CaptureMustNotMistakeWatchdogTimeoutForCompletedOperationFailure()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var watchdogEscaped = false;
            try { _ = await CaptureAsync(pending.Task, TimeSpan.Zero); }
            catch (TimeoutException) { watchdogEscaped = true; }
            Ensure(watchdogEscaped && !pending.Task.IsCompleted,
                "an unfinished operation's watchdog must escape rather than become a captured cleanup failure");
        }
        finally { pending.TrySetResult(); }

        var operationFailure = new TimeoutException("the completed operation itself failed");
        Ensure(ReferenceEquals(await CaptureAsync(Task.FromException(operationFailure)), operationFailure),
            "a completed operation's own TimeoutException must retain its identity");
    }

    private static Task<Exception?> CaptureAsync(Task task) => CaptureAsync(task, Timeout);

    private static async Task<Exception?> CaptureAsync(Task task, TimeSpan watchdog)
    {
        // Wait for completion without propagating the operation's exception into
        // the watchdog boundary. Even a completion/timeout race cannot confuse
        // WaitAsync's TimeoutException with an exception from the operation.
        var completed = task.ContinueWith(
            static finished => { _ = finished.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await completed.WaitAsync(watchdog);
        try { await task; return null; }
        catch (Exception exception) { return exception; }
    }

    private static bool Contains(Exception? exception, Exception expected)
    {
        if (ReferenceEquals(exception, expected)) return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                if (Contains(inner, expected)) return true;
        }
        return exception?.InnerException is { } cause && Contains(cause, expected);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void WriteFrame(TrackingWriter writer, ProtocolV2FrameType type, ulong id, int payloadBytes)
    {
        using (writer.BeginPacketScope(type, ProtocolV2FrameFlags.None, id))
        {
            writer.GetSpan(payloadBytes)[..payloadBytes].Clear();
            writer.Advance(payloadBytes);
        }
    }

    private sealed class Fixture
    {
        private readonly Pipe _input = new();
        private readonly SharpLinkRuntimeContext _context;
        private readonly List<TrackingWriter> _writers = [];
        internal GateOutput Output { get; }
        internal RpcSession Session { get; }
        internal TaskCompletionSource<Exception?> Disconnected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Fixture(string? outputFailure = null)
        {
            _context = new SharpLinkRuntimeContextBuilder().Configure(options =>
            {
                options.FlowControl.MaxSendQueueBytes = 1024;
                options.PerformanceProfile = SharpLinkPerformanceProfile.Throughput;
            }).Build(includeGeneratedAssemblyCatalog: false);
            Output = new GateOutput(outputFailure);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                Guid.NewGuid().ToString(), _input.Reader, Output, RpcSessionTestFixture.ServerOptions(_context));
            Session.OnDisconnected += exception => Disconnected.TrySetResult(exception);
        }

        internal TrackingWriter Progress(Exception? failure = null, bool blockReturn = false)
            => Frame(ProtocolV2FrameType.Ping, 0, 8, failure, blockReturn);

        internal TrackingWriter Normal(ulong id, Exception? failure = null, int payloadBytes = 32)
            => Frame(ProtocolV2FrameType.Response, id, payloadBytes, failure, false);

        private TrackingWriter Frame(ProtocolV2FrameType type, ulong id, int payloadBytes,
            Exception? failure, bool blockReturn)
        {
            var writer = new TrackingWriter(failure, blockReturn);
            _writers.Add(writer);
            WriteFrame(writer, type, id, payloadBytes);
            return writer;
        }

        internal async Task CleanupAsync()
        {
            foreach (var writer in _writers) writer.ReleaseReturn();
            Output.ReleaseFirst();
            Output.ReleaseSecond();
            // Test bodies explicitly inspect expected disposal failures. Cleanup must
            // also unblock resources when an earlier assertion demonstrates a defect.
            _ = await CaptureAsync(Session.DisposeAsync().AsTask());
            await _input.Writer.CompleteAsync();
            foreach (var writer in _writers) writer.DisposeGate();
            _context.Dispose();
        }
    }

    private sealed class GateOutput(string? failureMode) : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly TaskCompletionSource _firstRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _flushCount;
        private int _advanceCount;
        internal TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception Failure { get; } = new IOException("injected second flush failure");
        internal int FlushCount => Volatile.Read(ref _flushCount);
        internal int AdvanceCount => Volatile.Read(ref _advanceCount);
        internal void ReleaseFirst() => _firstRelease.TrySetResult();
        internal void ReleaseSecond() => _secondRelease.TrySetResult();
        public override void Advance(int bytes) { Interlocked.Increment(ref _advanceCount); _buffer.Advance(bytes); }
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);
        public override void CancelPendingFlush() { ReleaseFirst(); ReleaseSecond(); }
        public override void Complete(Exception? exception = null) { ReleaseFirst(); ReleaseSecond(); }
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _flushCount);
            if (count == 1)
            {
                FirstEntered.TrySetResult();
                await _firstRelease.Task.WaitAsync(Timeout);
            }
            else if (count == 2)
            {
                SecondEntered.TrySetResult();
                await _secondRelease.Task.WaitAsync(Timeout);
                if (failureMode == "throw") throw Failure;
                if (failureMode == "canceled") return new FlushResult(true, false);
                if (failureMode == "completed") return new FlushResult(false, true);
            }
            return new FlushResult(false, false);
        }
    }

    private sealed class TrackingWriter(Exception? returnFailure, bool blockReturn) : IRpcByteBufferWriter
    {
        private static long _returnSequence;
        private byte[] _buffer = new byte[2048];
        private readonly int[] _returns = new int[3];
        private readonly ManualResetEventSlim _release = new(!blockReturn);
        private int _written;
        private int _active = 1;
        private int _lease = 1;
        private int _returnCount;
        private long _firstReturnSequence;
        internal TaskCompletionSource ReturnEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReturnCount => Volatile.Read(ref _returnCount);
        internal int Lease => Volatile.Read(ref _lease);
        internal long FirstReturnSequence => Volatile.Read(ref _firstReturnSequence);
        internal int ReturnsForLease(int lease) => Volatile.Read(ref _returns[lease]);
        internal void ReleaseReturn() => _release.Set();
        internal void DisposeGate() => _release.Dispose();
        internal void BeginLease()
        {
            Ensure(Interlocked.CompareExchange(ref _active, 1, 0) == 0, "only a returned writer can be rerented");
            Interlocked.Increment(ref _lease);
            _written = 0;
        }
        public int WrittenCount => _written;
        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);
        public Span<byte> WrittenSpan => _buffer.AsSpan(0, _written);
        public int Capacity => _buffer.Length;
        public void Advance(int count) => _written += count;
        public void Clear() => _written = 0;
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (_buffer.Length - _written < sizeHint) Array.Resize(ref _buffer, _written + sizeHint);
            return _buffer.AsMemory(_written);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public void Dispose()
        {
            Interlocked.CompareExchange(ref _firstReturnSequence, Interlocked.Increment(ref _returnSequence), 0);
            Interlocked.Increment(ref _returnCount);
            Interlocked.Increment(ref _returns[_lease]);
            ReturnEntered.TrySetResult();
            if (!_release.Wait(Timeout)) throw new TimeoutException("test writer return was not released");
            Ensure(Interlocked.Exchange(ref _active, 0) == 1, "a writer lease was returned more than once");
            if (returnFailure is not null) throw returnFailure;
        }
    }

    private sealed class ThrowingFailureObserver(Exception exception) : IRequestEmissionFailureObserver
    {
        internal int Count;
        internal long RequestId;
        internal Exception? Failure;
        public void OnRequestEmissionFailure(long requestId, Exception failure)
        {
            Count++;
            RequestId = requestId;
            Failure = failure;
            throw exception;
        }
    }
}
