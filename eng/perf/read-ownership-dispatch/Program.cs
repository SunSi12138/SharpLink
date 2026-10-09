using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using SharpLink.Runtime;

if (args.Length is < 3 or > 4 || !int.TryParse(args[2], out var iterations) || iterations < 1000)
    throw new ArgumentException("Usage: <arm> <output.json> <iterations >= 1000> [main|backlog]");
var suite = args.Length == 4 ? args[3] : "main";
if (suite is not ("main" or "backlog")) throw new ArgumentException("Unknown suite.");
if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0")
    throw new InvalidOperationException("Set DOTNET_TieredCompilation=0.");
var commit = Environment.GetEnvironmentVariable("SHARPLINK_COMMIT");
if (string.IsNullOrWhiteSpace(commit))
    throw new InvalidOperationException("Set SHARPLINK_COMMIT to the tested source identity.");
if (Thread.CurrentThread.IsThreadPoolThread)
    throw new InvalidOperationException("Run the harness from the synchronous process entry point.");
const int warmupIterations = 5000;
var rows = new List<MeasurementRow>();
foreach (var specification in Cases.All.Where(item => item.Backlog == (suite == "backlog")))
{
    foreach (var wrapped in new[] { false, true })
    {
        using var fixture = new Fixture(specification, wrapped);
        fixture.Run(warmupIterations);
        fixture.DrainAll();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        fixture.DrainAll();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var before = fixture.Snapshot();
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var start = Stopwatch.GetTimestamp();
        fixture.Run(iterations);
        var quiescenceSeconds = fixture.DrainAll();
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var after = fixture.Snapshot();
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var counts = after - before;
        fixture.VerifyCounts(counts, iterations);
        var row = new MeasurementRow(
            specification.Id, specification.Category, specification.Consumer, specification.Backend,
            wrapped, iterations, elapsed.TotalSeconds, quiescenceSeconds, elapsed.TotalNanoseconds / iterations,
            iterations / elapsed.TotalSeconds, allocated, allocated / (double)iterations,
            cpu.TotalNanoseconds / iterations, GC.CollectionCount(0) - gen0,
            GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2,
            counts.Reads, counts.Results, counts.Advances, counts.Consumers,
            counts.Registered, counts.Callbacks, specification.Adapter ? iterations : 0,
            counts.Notifications, specification.SpecialContext, specification.Backlog,
            true, fixture.ThreadPoolBlockVerified, counts.ContextObservation);
        if (!double.IsFinite(row.QuiescenceSeconds) || row.QuiescenceSeconds < 0 || row.QuiescenceSeconds > row.ElapsedSeconds ||
            !double.IsFinite(row.ElapsedSeconds) || row.ElapsedSeconds <= 0 ||
            !double.IsFinite(row.NanosecondsPerOperation) || !double.IsFinite(row.OperationsPerSecond) ||
            !double.IsFinite(row.AllocatedBytesPerOperation) || !double.IsFinite(row.CpuNanosecondsPerOperation) ||
            allocated < 0 || row.CpuNanosecondsPerOperation < 0)
            throw new InvalidOperationException("Invalid measurement.");
        rows.Add(row);
    }
}
var construction = new[] { MeasureConstruction(false), MeasureConstruction(true) };
var document = new
{
    schemaVersion = 1,
    benchmark = "read-ownership-dispatch",
    suite,
    arm = args[0], sourceCommit = commit,
    runtime = RuntimeInformation.FrameworkDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    processorCount = Environment.ProcessorCount,
    serverGc = GCSettings.IsServerGC,
    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
    allocationScope = "process-wide-precise",
    iterations, warmupIterations, rows, construction,
    notes = new[]
    {
        "One fresh process is one observation; the outer runner controls arm ordering. Cells are fixed raw-then-wrapped pairs, not randomized within a process.",
        "Raw is the same instrumented ManualResetValueTaskSourceCore-backed PipeReader, using the identical consumer, scheduling, payload, validation and handshake. No wrapper-only async switch is used.",
        "All steady-state fixture, delegates, events, threads, contexts and scheduler construction is outside counters. Per-read async state machines and required BCL scheduling allocations are included in both raw and wrapped totals.",
        "GC.GetTotalAllocatedBytes(precise:true) includes worker threads. Counts end after exact consumer/source callback/custom queue drains and two stable samples of an empty/inactive ThreadPool, separated by Thread.Yield. This sampled quiescence fence is inside the ending time/allocation boundary and separately timed as quiescenceSeconds; there is no fixed delay interval; no claim about every runtime housekeeping instruction or thread-local allocation is made.",
        "Raw-to-wrapped allocated-byte differences attribute incremental wrapper cost within an identical cell; never subtract raw latency or infer an RPC speedup from this micro harness.",
        "Custom TaskScheduler registration and publisher-task cells include one ordinary Task per read in both controls. Custom SynchronizationContext queue storage is preallocated and reused.",
        "Explicit continuation adapter cells use cached delegates and consume each read once. Backlog callbacks are notification-only: the main thread deliberately polls, consumes and rearms before those callbacks execute.",
        "The backlog case temporarily limits this isolated process to one ThreadPool worker, blocks that worker, verifies no callback executes while blocked, releases and drains all notifications, then restores the original pool settings. A dedicated watchdog fails the process on a stuck run.",
        "Every result payload/status, source consumption and AdvanceTo is validated exactly once. No ValueTask token/status is inspected after consumption.",
        "Construction includes the fixed instrumented inner reader in both totals and excludes the preallocated retention array. It is separate from steady-state cost and is not a retained-graph-size measurement.",
        "This harness does not throw from callbacks and does not replace dispatch-exception subprocess tests or result/exception lifetime regression tests."
    }
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(document,
    new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

static ConstructionRow MeasureConstruction(bool wrapped)
{
    const int count = 10000;
    var readers = new PipeReader[count];
    _ = new ReadOwnershipPipeReader(new ControlledReader(false));
    var before = GC.GetTotalAllocatedBytes(precise: true);
    for (var index = 0; index < count; index++)
    {
        var inner = new ControlledReader(false);
        readers[index] = wrapped ? new ReadOwnershipPipeReader(inner) : inner;
    }
    var bytes = GC.GetTotalAllocatedBytes(precise: true) - before;
    GC.KeepAlive(readers);
    return new ConstructionRow(wrapped, count, bytes, bytes / (double)count);
}

sealed record MeasurementRow(string CaseId, string Category, string Consumer, string Backend,
    bool Wrapped, int Iterations, double ElapsedSeconds, double QuiescenceSeconds, double NanosecondsPerOperation,
    double OperationsPerSecond, long AllocatedBytes, double AllocatedBytesPerOperation,
    double CpuNanosecondsPerOperation, int Gen0, int Gen1, int Gen2,
    long Reads, long ResultConsumptions, long Advances, long ConsumerCompletions,
    long InnerCallbacksRegistered, long InnerCallbacksCompleted,
    long NotificationCallbacksExpected, long NotificationCallbacksCompleted,
    bool SpecialContext, bool Backlog, bool Drained, bool ThreadPoolBlockVerified, ContextObservation ContextObservation);
sealed record ConstructionRow(bool Wrapped, int Count, long AllocatedBytes, double AllocatedBytesPerReader);
sealed record CaseSpec(string Id, string Category, string Consumer, string Backend,
    bool Adapter = false, bool SpecialContext = false, bool Backlog = false);
static class Cases
{
    public static readonly CaseSpec[] All =
    [
        new("ordinary-await-before-completion", "ordinary", "async-method-per-read", "inline-framework-source/default-context"),
        new("completion-before-ordinary-await", "ordinary", "async-method-per-read", "completed-framework-source/default-context"),
        new("forced-async-inner-await", "ordinary", "async-method-per-read", "async-framework-source/threadpool"),
        new("custom-synchronization-context-await", "special", "async-method-per-read", "inline-framework-source/dedicated-synchronization-context", SpecialContext: true),
        new("custom-task-scheduler-await", "special", "async-method-per-read", "inline-framework-source/dedicated-task-scheduler", SpecialContext: true),
        new("publisher-synchronization-context-await", "special", "async-method-per-read", "inline-framework-source/nondefault-publisher-context", SpecialContext: true),
        new("publisher-task-await", "ordinary", "async-method-per-read", "inline-framework-source/task-publisher", SpecialContext: true),
        new("explicit-late-unsafe-continuation", "special", "cached-direct-unsafe-continuation", "completed-framework-source/threadpool", Adapter: true),
        new("explicit-flow-execution-context", "special", "cached-direct-flowing-continuation", "inline-framework-source/explicit-execution-context", Adapter: true, SpecialContext: true),
        new("reentrant-ordinary-await", "special", "single-async-loop", "inline-framework-source/reentrant-next-read"),
        new("late-continuation-backlog", "special", "cached-notification-plus-poll-consumer", "completed-framework-source/one-blocked-threadpool-worker", Adapter: true, Backlog: true)
    ];
}

readonly record struct Counts(long Reads, long Results, long Advances, long Consumers,
    long Registered, long Callbacks, long Notifications, ContextObservation ContextObservation)
{
    public static Counts operator -(Counts a, Counts b) => new(a.Reads - b.Reads,
        a.Results - b.Results, a.Advances - b.Advances, a.Consumers - b.Consumers,
        a.Registered - b.Registered, a.Callbacks - b.Callbacks, a.Notifications - b.Notifications,
        a.ContextObservation - b.ContextObservation);
}

readonly record struct ContextObservation(long NoSynchronizationContextCount, long CapturedSynchronizationContextCount,
    long PublisherSynchronizationContextCount, long OtherSynchronizationContextCount,
    long DefaultTaskSchedulerCount, long CapturedTaskSchedulerCount, long OtherTaskSchedulerCount,
    long TaskIdPresentCount, long ThreadPoolThreadCount)
{
    public static ContextObservation operator -(ContextObservation a, ContextObservation b) => new(
        a.NoSynchronizationContextCount - b.NoSynchronizationContextCount,
        a.CapturedSynchronizationContextCount - b.CapturedSynchronizationContextCount,
        a.PublisherSynchronizationContextCount - b.PublisherSynchronizationContextCount,
        a.OtherSynchronizationContextCount - b.OtherSynchronizationContextCount,
        a.DefaultTaskSchedulerCount - b.DefaultTaskSchedulerCount,
        a.CapturedTaskSchedulerCount - b.CapturedTaskSchedulerCount,
        a.OtherTaskSchedulerCount - b.OtherTaskSchedulerCount,
        a.TaskIdPresentCount - b.TaskIdPresentCount, a.ThreadPoolThreadCount - b.ThreadPoolThreadCount);
}

sealed class Fixture : IDisposable
{
    private static readonly AsyncLocal<int> FlowMarker = new();
    private readonly CaseSpec _spec;
    private readonly bool _wrapped;
    private readonly ControlledReader _inner;
    private readonly PipeReader _reader;
    private readonly Action _startConsumer;
    private readonly Action _publish;
    private readonly Action _consumeContinuation;
    private readonly Action _notify;
    private readonly DedicatedContext? _context;
    private readonly DedicatedScheduler? _scheduler;
    private readonly NonDefaultContext _nonDefaultPublisher = new();
    private readonly PoolBlocker? _blocker;
    private ValueTask _consumer;
    private ConfiguredValueTaskAwaitable<ReadResult>.ConfiguredValueTaskAwaiter _awaiter;
    private long _consumers;
    private long _notifications;
    private long _started;
    private long _loopArmed;
    private long _noContext, _capturedContext, _publisherContext, _otherContext;
    private long _defaultScheduler, _capturedScheduler, _otherScheduler, _taskIdPresent, _poolThread;
    private int _flowExpected;
    private Exception? _callbackError;
    public bool ThreadPoolBlockVerified { get; private set; }

    public Fixture(CaseSpec specification, bool wrapped)
    {
        _spec = specification;
        _wrapped = wrapped;
        _inner = new ControlledReader(specification.Id == "forced-async-inner-await");
        _reader = wrapped ? new ReadOwnershipPipeReader(_inner) : _inner;
        _startConsumer = StartConsumer;
        _publish = _inner.Finish;
        _consumeContinuation = ConsumeContinuation;
        _notify = Notify;
        if (specification.Id == "custom-synchronization-context-await")
            _context = new DedicatedContext();
        if (specification.Id == "custom-task-scheduler-await")
            _scheduler = new DedicatedScheduler();
        if (specification.Backlog)
            _blocker = new PoolBlocker();
    }

    public Counts Snapshot() => new(_inner.Reads, _inner.Results, _inner.Advances,
        Volatile.Read(ref _consumers), _inner.Registered, _inner.Callbacks,
        Volatile.Read(ref _notifications), new ContextObservation(_noContext, _capturedContext,
            _publisherContext, _otherContext, _defaultScheduler, _capturedScheduler,
            _otherScheduler, _taskIdPresent, _poolThread));

    public void Run(int count)
    {
        if (_spec.Backlog) { RunBacklog(count); return; }
        if (_spec.Id == "reentrant-ordinary-await") { RunLoop(count); return; }
        for (var index = 0; index < count; index++)
        {
            var notificationTarget = Volatile.Read(ref _notifications) + 1;
            switch (_spec.Id)
            {
                case "completion-before-ordinary-await":
                    var pending = _reader.ReadAsync();
                    if (pending.IsCompleted) throw new InvalidOperationException("Expected suspension.");
                    _inner.Finish();
                    WaitRead(ref pending);
                    _consumer = ConsumeGivenAsync(pending, captureContext: false);
                    break;
                case "explicit-late-unsafe-continuation":
                    PrepareAdapter();
                    _inner.Finish();
                    WaitAwaiter();
                    _awaiter.UnsafeOnCompleted(_consumeContinuation);
                    WaitNotifications(notificationTarget);
                    break;
                case "explicit-flow-execution-context":
                    PrepareAdapter();
                    _flowExpected = 73;
                    FlowMarker.Value = _flowExpected;
                    _awaiter.OnCompleted(_consumeContinuation);
                    FlowMarker.Value = 0;
                    _inner.Finish();
                    WaitNotifications(notificationTarget);
                    break;
                default:
                    RegisterConsumer();
                    if (_consumer.IsCompleted) throw new InvalidOperationException("Consumer did not suspend.");
                    Publish();
                    break;
            }
            if (!_spec.Adapter)
            {
                WaitConsumer();
                _consumer.GetAwaiter().GetResult();
            }
            Drain();
        }
    }

    private void RegisterConsumer()
    {
        if (_context is not null)
        {
            var target = Volatile.Read(ref _started) + 1;
            _context.PostAction(_startConsumer);
            WaitUntil(ref _started, target);
        }
        else if (_scheduler is not null)
        {
            Task.Factory.StartNew(_startConsumer, CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, _scheduler).GetAwaiter().GetResult();
        }
        else StartConsumer();
    }

    private void StartConsumer()
    {
        _consumer = ConsumeGivenAsync(_reader.ReadAsync(), captureContext: true);
        Interlocked.Increment(ref _started);
    }

    private async ValueTask ConsumeGivenAsync(ValueTask<ReadResult> pending, bool captureContext)
    {
        var result = await pending.ConfigureAwait(captureContext);
        ValidateAndAdvance(result);
    }

    private void Publish()
    {
        if (_spec.Id == "publisher-synchronization-context-await")
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(_nonDefaultPublisher);
            try { _inner.Finish(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        else if (_spec.Id == "publisher-task-await")
            Task.Run(_publish).GetAwaiter().GetResult();
        else _inner.Finish();
    }

    private void PrepareAdapter()
    {
        _awaiter = _reader.ReadAsync().ConfigureAwait(false).GetAwaiter();
        if (_awaiter.IsCompleted) throw new InvalidOperationException("Expected suspension.");
    }

    private void ConsumeContinuation()
    {
        try
        {
            if (_spec.Id == "explicit-flow-execution-context" && FlowMarker.Value != _flowExpected)
                throw new InvalidOperationException("ExecutionContext was not preserved.");
            ValidateAndAdvance(_awaiter.GetResult());
        }
        catch (Exception exception) { Interlocked.CompareExchange(ref _callbackError, exception, null); }
        finally { Interlocked.Increment(ref _notifications); }
    }

    private void Notify() => Interlocked.Increment(ref _notifications);

    private void ValidateAndAdvance(ReadResult result)
    {
        if (_context is not null && !ReferenceEquals(SynchronizationContext.Current, _context))
            throw new InvalidOperationException("SynchronizationContext was not preserved.");
        if (_scheduler is not null && !ReferenceEquals(TaskScheduler.Current, _scheduler))
            throw new InvalidOperationException("TaskScheduler was not preserved.");
        if (result.Buffer.Length != 1 || result.IsCanceled || result.IsCompleted ||
            result.Buffer.FirstSpan[0] != unchecked((byte)(_consumers + 1)))
            throw new InvalidOperationException("Unexpected read payload, status, or order.");
        var context = SynchronizationContext.Current;
        if (context is null) _noContext++;
        else if (ReferenceEquals(context, _context)) _capturedContext++;
        else if (ReferenceEquals(context, _nonDefaultPublisher)) _publisherContext++;
        else _otherContext++;
        var scheduler = TaskScheduler.Current;
        if (ReferenceEquals(scheduler, TaskScheduler.Default)) _defaultScheduler++;
        else if (ReferenceEquals(scheduler, _scheduler)) _capturedScheduler++;
        else _otherScheduler++;
        if (Task.CurrentId is not null) _taskIdPresent++;
        if (Thread.CurrentThread.IsThreadPoolThread) _poolThread++;
        _reader.AdvanceTo(result.Buffer.End);
        Interlocked.Increment(ref _consumers);
    }

    private async ValueTask ConsumeLoopAsync(int count)
    {
        for (var index = 0; index < count; index++)
        {
            var pending = _reader.ReadAsync();
            Interlocked.Increment(ref _loopArmed);
            ValidateAndAdvance(await pending.ConfigureAwait(false));
        }
    }

    private void RunLoop(int count)
    {
        var first = Volatile.Read(ref _loopArmed);
        _consumer = ConsumeLoopAsync(count);
        for (var index = 0; index < count; index++)
        {
            WaitUntil(ref _loopArmed, first + index + 1);
            // ReadAsync and registration happen in the same inline MoveNext before Finish returns.
            // The first registration is complete when ConsumeLoopAsync returns to this method.
            _inner.WaitForRegistration(first + index + 1);
            _inner.Finish();
        }
        WaitConsumer();
        _consumer.GetAwaiter().GetResult();
        Drain();
    }

    private void RunBacklog(int count)
    {
        var notificationsBefore = Volatile.Read(ref _notifications);
        _blocker!.Begin();
        try
        {
            for (var index = 0; index < count; index++)
            {
                PrepareAdapter();
                _inner.Finish();
                WaitAwaiter();
                _awaiter.UnsafeOnCompleted(_notify);
                ValidateAndAdvance(_awaiter.GetResult());
                if (Volatile.Read(ref _notifications) != notificationsBefore)
                    throw new InvalidOperationException("Backlog notification ran while the pool blocker was active.");
            }
            ThreadPoolBlockVerified = true;
        }
        finally { _blocker.Release(); }
        WaitNotifications(notificationsBefore + count);
        Drain();
    }

    private void WaitNotifications(long target)
    {
        WaitUntil(ref _notifications, target);
        if (_callbackError is { } error) throw new InvalidOperationException("Continuation failed.", error);
    }

    private void WaitConsumer()
    {
        var spin = new SpinWait();
        var start = Stopwatch.GetTimestamp();
        while (!_consumer.IsCompleted) Pause(ref spin, start);
    }

    private static void WaitRead(ref ValueTask<ReadResult> pending)
    {
        var spin = new SpinWait();
        var start = Stopwatch.GetTimestamp();
        while (!pending.IsCompleted) Pause(ref spin, start);
    }

    private void WaitAwaiter()
    {
        var spin = new SpinWait();
        var start = Stopwatch.GetTimestamp();
        while (!_awaiter.IsCompleted) Pause(ref spin, start);
    }

    internal static void WaitUntil(ref long value, long target)
    {
        var spin = new SpinWait();
        var start = Stopwatch.GetTimestamp();
        while (Volatile.Read(ref value) < target) Pause(ref spin, start);
        if (Volatile.Read(ref value) != target)
            throw new InvalidOperationException("Duplicate completion or unexpected counter.");
    }

    private static void Pause(ref SpinWait spin, long start)
    {
        if (Stopwatch.GetElapsedTime(start).TotalSeconds > 30)
            throw new TimeoutException("Timed out waiting for benchmark work to drain.");
        spin.SpinOnce(sleep1Threshold: -1);
    }

    public void Drain()
    {
        _inner.Drain();
        _context?.Drain();
        _scheduler?.Drain();
        if (_callbackError is { } error) throw new InvalidOperationException("Continuation failed.", error);
    }

    public double DrainAll()
    {
        Drain();
        var started = Stopwatch.GetTimestamp();
        QuiesceThreadPool();
        return Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    private static void QuiesceThreadPool()
    {
        if (Thread.CurrentThread.IsThreadPoolThread)
            throw new InvalidOperationException("ThreadPool quiescence requires non-pool orchestration.");
        var spin = new SpinWait();
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            if (PoolIsIdle())
            {
                Thread.Yield();
                if (PoolIsIdle()) return;
            }
            Pause(ref spin, start);
        }
    }

    private static bool PoolIsIdle()
    {
        ThreadPool.GetMaxThreads(out var maximum, out _);
        ThreadPool.GetAvailableThreads(out var available, out _);
        return ThreadPool.PendingWorkItemCount == 0 && maximum == available;
    }

    public void VerifyCounts(Counts counts, int count)
    {
        var context = counts.ContextObservation;
        if (context.NoSynchronizationContextCount + context.CapturedSynchronizationContextCount +
            context.PublisherSynchronizationContextCount + context.OtherSynchronizationContextCount != count ||
            context.DefaultTaskSchedulerCount + context.CapturedTaskSchedulerCount + context.OtherTaskSchedulerCount != count ||
            (_context is not null && context.CapturedSynchronizationContextCount != count) ||
            (_scheduler is not null && context.CapturedTaskSchedulerCount != count))
            throw new InvalidOperationException("Incorrect context observations.");
        var expectedInnerCallbacks = !_wrapped && _spec.Id == "completion-before-ordinary-await" ? 0 : count;
        if (counts.Reads != count || counts.Results != count || counts.Advances != count ||
            counts.Consumers != count || counts.Registered != expectedInnerCallbacks ||
            counts.Registered != counts.Callbacks ||
            counts.Notifications != (_spec.Adapter ? count : 0) ||
            (_spec.Backlog && !ThreadPoolBlockVerified))
            throw new InvalidOperationException($"Incorrect operation counts for {_spec.Id}: {counts}");
    }

    public void Dispose()
    {
        Drain();
        _reader.CompleteAsync().AsTask().GetAwaiter().GetResult();
        _inner.Drain();
        _context?.Dispose();
        _scheduler?.Dispose();
        _blocker?.Dispose();
        QuiesceThreadPool();
    }

    private sealed class NonDefaultContext : SynchronizationContext { }
}

sealed class ControlledReader : PipeReader, IValueTaskSource<ReadResult>
{
    private static readonly Action<object?> Trampoline = static state => ((ControlledReader)state!).Invoke();
    private readonly byte[] _payload = new byte[1];
    private readonly ReadOnlySequence<byte> _buffer;
    private ManualResetValueTaskSourceCore<ReadResult> _source;
    private Action<object?>? _continuation;
    private object? _continuationState;
    private int _active;
    private int _consumed;
    private long _reads, _results, _advances, _registered, _callbacks;
    public long Reads => Volatile.Read(ref _reads);
    public long Results => Volatile.Read(ref _results);
    public long Advances => Volatile.Read(ref _advances);
    public long Registered => Volatile.Read(ref _registered);
    public long Callbacks => Volatile.Read(ref _callbacks);
    public ControlledReader(bool asynchronous)
    {
        _buffer = new ReadOnlySequence<byte>(_payload);
        _source.RunContinuationsAsynchronously = asynchronous;
    }
    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _active, 1) != 0) throw new InvalidOperationException("Overlapping read.");
        _consumed = 0;
        _payload[0] = unchecked((byte)Interlocked.Increment(ref _reads));
        _source.Reset();
        return new ValueTask<ReadResult>(this, _source.Version);
    }
    public void Finish() => _source.SetResult(new ReadResult(_buffer, false, false));
    public override void AdvanceTo(SequencePosition consumed)
    {
        if (!consumed.Equals(_buffer.End) || Volatile.Read(ref _consumed) != 1 ||
            Interlocked.Exchange(ref _active, 0) != 1)
            throw new InvalidOperationException("Invalid or duplicate AdvanceTo.");
        Interlocked.Increment(ref _advances);
    }
    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => AdvanceTo(consumed);
    public ReadResult GetResult(short token)
    {
        var result = _source.GetResult(token);
        if (Interlocked.Exchange(ref _consumed, 1) != 0) throw new InvalidOperationException("Duplicate consumption.");
        Interlocked.Increment(ref _results);
        return result;
    }
    public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        _continuation = continuation;
        _continuationState = state;
        _source.OnCompleted(Trampoline, this, token, flags);
        Interlocked.Increment(ref _registered);
    }
    private void Invoke()
    {
        // Snapshot before invoking: a reentrant callback may arm/register the following read.
        var continuation = _continuation!;
        var state = _continuationState;
        try { continuation(state); }
        finally { Interlocked.Increment(ref _callbacks); }
    }
    public void WaitForRegistration(long target) => Fixture.WaitUntil(ref _registered, target);
    public void Drain() => Fixture.WaitUntil(ref _callbacks, Volatile.Read(ref _registered));
    public override void Complete(Exception? exception = null)
    {
        if (Volatile.Read(ref _active) != 0) throw new InvalidOperationException("Reader still active.");
    }
    public override void CancelPendingRead()
    {
        if (Volatile.Read(ref _active) != 0) throw new InvalidOperationException("Unexpected cancellation during active read.");
    }
    public override bool TryRead(out ReadResult result) => throw new NotSupportedException();
}

sealed class DedicatedContext : SynchronizationContext, IDisposable
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new(16);
    private readonly AutoResetEvent _ready = new(false);
    private readonly Thread _thread;
    private bool _stopping;
    private long _posted, _completed;
    private Exception? _error;
    private static readonly SendOrPostCallback InvokeAction = static state => ((Action)state!)();
    public DedicatedContext()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "dispatch-micro-context" };
        _thread.Start();
    }
    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_queue) { _queue.Enqueue((d, state)); Interlocked.Increment(ref _posted); }
        _ready.Set();
    }
    public void PostAction(Action action) => Post(InvokeAction, action);
    private void Run()
    {
        SetSynchronizationContext(this);
        while (true)
        {
            (SendOrPostCallback Callback, object? State) work;
            lock (_queue)
            {
                if (_queue.TryDequeue(out work)) { }
                else if (_stopping) return;
                else work = default;
            }
            if (work.Callback is null) { _ready.WaitOne(); continue; }
            try { work.Callback(work.State); }
            catch (Exception exception) { _error = exception; }
            finally { Interlocked.Increment(ref _completed); }
        }
    }
    public void Drain()
    {
        Fixture.WaitUntil(ref _completed, Volatile.Read(ref _posted));
        if (_error is { } error) throw new InvalidOperationException("Context worker failed.", error);
    }
    public void Dispose()
    {
        Drain();
        lock (_queue) _stopping = true;
        _ready.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Context did not stop.");
        _ready.Dispose();
    }
}

sealed class DedicatedScheduler : TaskScheduler, IDisposable
{
    private readonly Queue<Task> _queue = new(16);
    private readonly AutoResetEvent _ready = new(false);
    private readonly Thread _thread;
    private bool _stopping;
    private long _posted, _completed;
    public DedicatedScheduler()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "dispatch-micro-scheduler" };
        _thread.Start();
    }
    protected override IEnumerable<Task>? GetScheduledTasks() { lock (_queue) return _queue.ToArray(); }
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    protected override void QueueTask(Task task)
    {
        lock (_queue) { _queue.Enqueue(task); Interlocked.Increment(ref _posted); }
        _ready.Set();
    }
    private void Run()
    {
        while (true)
        {
            Task? task;
            lock (_queue)
            {
                if (_queue.TryDequeue(out task)) { }
                else if (_stopping) return;
            }
            if (task is null) { _ready.WaitOne(); continue; }
            TryExecuteTask(task);
            Interlocked.Increment(ref _completed);
        }
    }
    public void Drain() => Fixture.WaitUntil(ref _completed, Volatile.Read(ref _posted));
    public void Dispose()
    {
        Drain();
        lock (_queue) _stopping = true;
        _ready.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Scheduler did not stop.");
        _ready.Dispose();
    }
}

sealed class PoolBlocker : IThreadPoolWorkItem, IDisposable
{
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private readonly ManualResetEventSlim _done = new(false);
    private readonly ManualResetEventSlim _stopWatchdog = new(false);
    private readonly Thread _watchdog;
    private readonly int _minWorker, _minIo, _maxWorker, _maxIo;
    private bool _active;
    public PoolBlocker()
    {
        ThreadPool.GetMinThreads(out _minWorker, out _minIo);
        ThreadPool.GetMaxThreads(out _maxWorker, out _maxIo);
        if (!ThreadPool.SetMinThreads(1, _minIo)) throw new InvalidOperationException("Cannot set pool minimum.");
        if (!ThreadPool.SetMaxThreads(1, _maxIo))
        {
            ThreadPool.SetMinThreads(_minWorker, _minIo);
            throw new InvalidOperationException("Cannot guarantee single-worker backlog.");
        }
        _watchdog = new Thread(() =>
        {
            if (!_stopWatchdog.Wait(TimeSpan.FromMinutes(2)))
                Environment.FailFast("Dispatch micro backlog watchdog expired.");
        }) { IsBackground = true, Name = "dispatch-micro-backlog-watchdog" };
        _watchdog.Start();
    }
    public void Begin()
    {
        _entered.Reset(); _release.Reset(); _done.Reset(); _active = true;
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        if (!_entered.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Pool blocker did not enter.");
    }
    public void Execute()
    {
        _entered.Set();
        _release.Wait();
        _done.Set();
    }
    public void Release()
    {
        _release.Set();
        if (!_done.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Pool blocker did not exit.");
        _active = false;
    }
    public void Dispose()
    {
        try { if (_active) Release(); }
        finally
        {
            var maxRestored = ThreadPool.SetMaxThreads(_maxWorker, _maxIo);
            var minRestored = ThreadPool.SetMinThreads(_minWorker, _minIo);
            _stopWatchdog.Set();
            _watchdog.Join();
            _entered.Dispose(); _release.Dispose(); _done.Dispose(); _stopWatchdog.Dispose();
            if (!maxRestored || !minRestored) throw new InvalidOperationException("Cannot restore pool settings.");
        }
    }
}
