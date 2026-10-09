using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Exercises process-fatal continuation failures outside the test runner. The child observes the
/// real unhandled-exception path and lets fatal errors terminate naturally, rather than
/// replacing production exception reporting with a test seam.
/// </summary>
[NotInParallel("read-ownership-dispatch-probes")]
public class ReadOwnershipPipeReaderDispatchProbeTests
{
    private const string ProbeEnvironmentVariable = "SHARPLINK_READ_OWNERSHIP_DISPATCH_PROBE";
    private const string SuccessMarker = "READ_OWNERSHIP_DISPATCH_PROBE_PASSED";
    private const string FailureMarker = "READ_OWNERSHIP_DISPATCH_PROBE_FAILED";
    private const int ProbeTimeoutSeconds = 15;
    private static UnhandledExceptionReport? _childReport;
    private static int _childStarted;
    private static readonly TaskCompletionSource ChildCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    [Timeout(90_000)]
    [Arguments("raw-retained")]
    [Arguments("raw-rearmed")]
    [Arguments("fault-rearmed")]
    [Arguments("scheduler-before")]
    [Arguments("scheduler-after")]
    [Arguments("post-before")]
    [Arguments("post-after")]
    [Arguments("context-before")]
    [Arguments("context-after")]
    [Arguments("deep-reentrancy")]
    [Arguments("late-backlog")]
    public async Task DispatchShouldPreserveExceptionAndOwnershipSemantics(string scenario, CancellationToken cancellationToken)
    {
        var childScenario = Environment.GetEnvironmentVariable(ProbeEnvironmentVariable);
        if (childScenario is not null)
        {
            // A plain thread guarantees no test-runner Task or scheduling context changes the
            // inline-dispatch cases. The serialized first child test owns the entire process.
            if (Interlocked.Exchange(ref _childStarted, 1) == 0)
                new Thread(() => RunDispatchProbe(childScenario)) { IsBackground = true }.Start();
            await ChildCompletion.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            return;
        }

        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("No process executable path."),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ReadOwnershipPipeReaderDispatchProbeTests).Assembly.Location);
        start.ArgumentList.Add("--treenode-filter");
        start.ArgumentList.Add("/*/*/ReadOwnershipPipeReaderDispatchProbeTests/*");
        start.Environment[ProbeEnvironmentVariable] = scenario;
        // A regression such as stack overflow should not create a multi-gigabyte dump in CI.
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";

        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start dispatch probe.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var timedOut = false;
        try
        {
            await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            timedOut = true;
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().ConfigureAwait(false);
            }
        }

        var output = await stdout.ConfigureAwait(false);
        var errors = await stderr.ConfigureAwait(false);
        var fatal = IsFatalScenario(scenario);
        var expectedMarker = SuccessLine(scenario, fatal);
        Ensure(!timedOut && (fatal ? child.ExitCode != 0 : child.ExitCode == 0) &&
            ContainsLine(output, expectedMarker) && !output.Contains(FailureMarker, StringComparison.Ordinal),
            $"Dispatch probe '{scenario}' failed (timeout={timedOut}, exit={child.ExitCode}).\nstdout:\n{output}\nstderr:\n{errors}");
    }

    // Register before TUnit's handler so the real fatal error is validated before the host can
    // terminate. Never run or wait for probes inside a module initializer: background callbacks
    // cannot enter this module until initialization has returned.
    [ModuleInitializer]
    internal static void RegisterChildExceptionHandler()
    {
        if (Environment.GetEnvironmentVariable(ProbeEnvironmentVariable) is null)
            return;
        _childReport = new UnhandledExceptionReport();
        AppDomain.CurrentDomain.UnhandledException += _childReport.OnUnhandledException;
    }

    private static void RunDispatchProbe(string scenario)
    {
        var report = _childReport ?? throw new InvalidOperationException("The child exception handler is missing.");
        report.Scenario = scenario;
        try
        {
            switch (scenario)
            {
                case "raw-retained": RunRawThrow(report, rearm: false, faultRead: false); break;
                case "raw-rearmed": RunRawThrow(report, rearm: true, faultRead: false); break;
                case "fault-rearmed": RunRawThrow(report, rearm: true, faultRead: true); break;
                case "scheduler-before": RunSchedulerThrow(report, completeFirst: false); break;
                case "scheduler-after": RunSchedulerThrow(report, completeFirst: true); break;
                case "post-before": RunPostThrow(report, completeFirst: false); break;
                case "post-after": RunPostThrow(report, completeFirst: true); break;
                case "context-before": RunContextPumpThrow(completeFirst: false); break;
                case "context-after": RunContextPumpThrow(completeFirst: true); break;
                case "deep-reentrancy": RunDeepReentrancy(); break;
                case "late-backlog": RunLateNotificationBacklog(); break;
                default: throw new InvalidOperationException($"Unknown dispatch probe '{scenario}'.");
            }
            WriteProbeLine(SuccessLine(scenario, fatal: false));
            ChildCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            WriteProbeLine($"{FailureMarker}|scenario={scenario}|exception={exception}");
            ChildCompletion.TrySetException(exception);
        }
    }

    private static void RunRawThrow(UnhandledExceptionReport report, bool rearm, bool faultRead)
    {
        var inner = new ProbePipeReader();
        var reader = new ReadOwnershipPipeReader(inner);
        var callbackError = new ApplicationException("raw callback marker");
        var readError = new IOException("inner fault marker");
        report.Expect(callbackError);
        var first = reader.ReadAsync();
        ValueTask<ReadResult> second = default;
        ReadResult heldResult = default;
        var producerThread = Environment.CurrentManagedThreadId;
        var callbackCount = 0;
        first.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
        {
            Ensure(Environment.CurrentManagedThreadId == producerThread, "The raw callback must run inline.");
            callbackCount++;
            if (faultRead)
            {
                Ensure(ReferenceEquals(Capture(() => first.GetAwaiter().GetResult()), readError),
                    "The read must preserve the inner fault independently of the callback failure.");
            }
            else
            {
                heldResult = first.GetAwaiter().GetResult();
                Ensure(heldResult.Buffer.FirstSpan[0] == 0x41, "The first arm's payload changed.");
                if (rearm)
                    reader.AdvanceTo(heldResult.Buffer.End);
            }
            if (rearm)
                second = reader.ReadAsync();
            throw callbackError;
        });

        // An escaping callback error fails here, before the reporting gate is opened.
        inner.Publish(0x41, faultRead ? readError : null);
        Ensure(callbackCount == 1, "The inline callback must run exactly once.");
        Ensure(Capture(() => reader.ReadAsync()) is InvalidOperationException,
            "A callback failure must not release retained or rearmed read ownership.");
        if (rearm)
        {
            Ensure(!second.IsCompleted, "The new arm must remain pending after the old callback fails.");
            Ensure(inner.AdvanceCount == (faultRead ? 0 : 1), "A faulted read must not require AdvanceTo.");
            inner.Publish(0x42);
            Consume(reader, second, 0x42);
        }
        else
        {
            Ensure(inner.ReadCount == 1, "Retained ownership must prevent reaching the inner reader.");
            reader.AdvanceTo(heldResult.Buffer.End);
        }
        VerifyNextRead(reader, inner);
        report.ProducerReturnedAndOwnershipVerified();
    }

    private static void RunSchedulerThrow(UnhandledExceptionReport report, bool completeFirst)
    {
        var inner = new ProbePipeReader();
        var reader = new ReadOwnershipPipeReader(inner);
        var failure = new ApplicationException("scheduled callback marker");
        report.Expect(failure);
        var read = reader.ReadAsync();
        if (completeFirst)
            inner.Publish(0x41);
        using var scheduler = new PumpTaskScheduler();
        var callbackCount = 0;
        var registration = Task.Factory.StartNew(() => read.GetAwaiter().UnsafeOnCompleted(() =>
        {
            Ensure(ReferenceEquals(TaskScheduler.Current, scheduler), "The captured scheduler was ignored.");
            callbackCount++;
            Consume(reader, read, 0x41);
            throw failure;
        }), CancellationToken.None, TaskCreationOptions.None, scheduler);
        Ensure(ReferenceEquals(scheduler.RunOne(), registration), "The registration task was not scheduled first.");
        registration.GetAwaiter().GetResult();
        if (!completeFirst)
            inner.Publish(0x41);
        var callbackTask = scheduler.RunOne();
        Ensure(callbackTask.IsCompletedSuccessfully,
            "A callback error must be reported asynchronously, not trapped in the scheduler's Task.");
        Ensure(callbackCount == 1, "The scheduled callback must run exactly once.");
        VerifyNextRead(reader, inner);
        report.ProducerReturnedAndOwnershipVerified();
    }

    private static void RunPostThrow(UnhandledExceptionReport report, bool completeFirst)
    {
        var inner = new ProbePipeReader();
        var reader = new ReadOwnershipPipeReader(inner);
        var failure = new ApplicationException("SynchronizationContext.Post marker");
        report.Expect(failure);
        var read = reader.ReadAsync();
        if (completeFirst)
            inner.Publish(0x41);
        using var context = new PumpSynchronizationContext(failure);
        var callbackCount = 0;
        WithContext(context, () => read.GetAwaiter().UnsafeOnCompleted(() => callbackCount++));
        if (!completeFirst)
            inner.Publish(0x41);
        Ensure(context.PostCount == 1 && callbackCount == 0,
            "Post must fail once without invoking the consumer callback.");
        Ensure(Capture(() => reader.ReadAsync()) is InvalidOperationException,
            "A scheduling error must not release the unread result's ownership.");
        Consume(reader, read, 0x41);
        VerifyNextRead(reader, inner);
        report.ProducerReturnedAndOwnershipVerified();
    }

    private static void RunContextPumpThrow(bool completeFirst)
    {
        var inner = new ProbePipeReader();
        var reader = new ReadOwnershipPipeReader(inner);
        var failure = new ApplicationException("queued context callback marker");
        var read = reader.ReadAsync();
        if (completeFirst)
            inner.Publish(0x41);
        using var context = new PumpSynchronizationContext();
        var callbackCount = 0;
        WithContext(context, () => read.GetAwaiter().UnsafeOnCompleted(() =>
        {
            Ensure(ReferenceEquals(SynchronizationContext.Current, context), "The captured context was ignored.");
            callbackCount++;
            Consume(reader, read, 0x41);
            throw failure;
        }));
        if (!completeFirst)
            inner.Publish(0x41);
        Ensure(callbackCount == 0, "A queued context callback must wait for its pump.");
        Ensure(ReferenceEquals(Capture(context.RunOne), failure),
            "Once Post succeeds, a callback error must escape that context's pump unchanged.");
        Ensure(callbackCount == 1 && context.PostCount == 1, "The context callback must run exactly once.");
        VerifyNextRead(reader, inner);
    }

    private static void RunDeepReentrancy()
    {
        const int targetReads = 100_000;
        var inner = new ProbePipeReader();
        var reader = new ReadOwnershipPipeReader(inner);
        using var finished = new ManualResetEventSlim();
        var completedReads = 0;
        ValueTask<ReadResult> pending = default;
        Action callback = null!;
        callback = () =>
        {
            Consume(reader, pending, 0x41);
            if (++completedReads == targetReads)
            {
                finished.Set();
                return;
            }
            pending = reader.ReadAsync();
            pending.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(callback);
            inner.Publish(0x41);
        };
        // A deliberate small initial stack exposes unbounded synchronous recursion without
        // relying on the test runner's platform-specific worker stack size.
        var producer = new Thread(() =>
        {
            pending = reader.ReadAsync();
            pending.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(callback);
            inner.Publish(0x41);
        }, 1024 * 1024)
        { IsBackground = true };
        producer.Start();
        Ensure(finished.Wait(TimeSpan.FromSeconds(ProbeTimeoutSeconds)),
            $"The reentrant chain stalled after {Volatile.Read(ref completedReads)} reads.");
        Ensure(producer.Join(TimeSpan.FromSeconds(ProbeTimeoutSeconds)), "The initial producer did not return.");
        Ensure(completedReads == targetReads && inner.AdvanceCount == targetReads,
            "Every reentrant read must be consumed and advanced exactly once.");
        VerifyNextRead(reader, inner);
    }

    private static void RunLateNotificationBacklog()
    {
        const int notificationCount = 128;
        ThreadPool.GetMinThreads(out _, out var minCompletionPorts);
        ThreadPool.GetMaxThreads(out _, out var maxCompletionPorts);
        Ensure(ThreadPool.SetMinThreads(1, minCompletionPorts) && ThreadPool.SetMaxThreads(1, maxCompletionPorts),
            "Could not isolate the child to one ThreadPool worker.");
        using var workerStarted = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var callbacksFinished = new CountdownEvent(notificationCount);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            workerStarted.Set();
            Ensure(releaseWorker.Wait(TimeSpan.FromSeconds(ProbeTimeoutSeconds)), "The backlog worker was not released.");
        }, state: (object?)null, preferLocal: false);
        Ensure(workerStarted.Wait(TimeSpan.FromSeconds(ProbeTimeoutSeconds)), "The blocking worker did not start.");
        var counts = new int[notificationCount];
        var inner = new ProbePipeReader();
        var reader = new ReadOwnershipPipeReader(inner);
        try
        {
            for (var index = 0; index < notificationCount; index++)
            {
                var read = reader.ReadAsync();
                inner.Publish(0x41);
                var callbackIndex = index;
                read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
                {
                    Ensure(Interlocked.Increment(ref counts[callbackIndex]) == 1,
                        $"Late callback {callbackIndex} ran more than once.");
                    callbacksFinished.Signal();
                });
                // Polling GetResult after registering is a legal single observation. The queued
                // callback intentionally only notifies, so the next arm can precede dispatch.
                Consume(reader, read, 0x41);
                Ensure(counts[index] == 0, "The sole worker must remain blocked while notifications accumulate.");
            }
        }
        finally
        {
            releaseWorker.Set();
        }
        Ensure(callbacksFinished.Wait(TimeSpan.FromSeconds(ProbeTimeoutSeconds)), "A queued late notification was lost.");
        for (var index = 0; index < notificationCount; index++)
            Ensure(counts[index] == 1, $"Late callback {index} did not run exactly once.");
        VerifyNextRead(reader, inner);
    }

    private static void VerifyNextRead(ReadOwnershipPipeReader reader, ProbePipeReader inner)
    {
        var read = reader.ReadAsync();
        Ensure(!read.IsCompleted, "The next independent read must start pending.");
        inner.Publish(0x43);
        Consume(reader, read, 0x43);
    }

    private static void Consume(ReadOwnershipPipeReader reader, ValueTask<ReadResult> read, byte expected)
    {
        var result = read.GetAwaiter().GetResult();
        Ensure(result.Buffer.Length == 1 && result.Buffer.FirstSpan[0] == expected, "The read payload was corrupted.");
        reader.AdvanceTo(result.Buffer.End);
    }

    private static void WithContext(SynchronizationContext context, Action action)
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static Exception? Capture(Action action)
    {
        try { action(); }
        catch (Exception exception) { return exception; }
        return null;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static bool IsFatalScenario(string scenario)
        => scenario is "raw-retained" or "raw-rearmed" or "fault-rearmed" or
            "scheduler-before" or "scheduler-after" or "post-before" or "post-after";

    private static string SuccessLine(string scenario, bool fatal)
        => fatal
            ? $"{SuccessMarker}|scenario={scenario}|fatal=true|terminating=true|identity=true|ownership=true"
            : $"{SuccessMarker}|scenario={scenario}|fatal=false";

    private static bool ContainsLine(string output, string expected)
    {
        using var lines = new StringReader(output);
        while (lines.ReadLine() is { } line)
        {
            if (string.Equals(line, expected, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void WriteProbeLine(string line)
    {
        // TUnit may capture Console.Out in the child; write the process protocol directly.
        using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        output.WriteLine(line);
    }

    private sealed class UnhandledExceptionReport
    {
        private readonly ManualResetEventSlim _producerAndStateVerified = new();
        private Exception? _expected;
        internal string Scenario { get; set; } = "uninitialized";

        internal void Expect(Exception exception) => _expected = exception;

        internal void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            var matches = _expected is not null && ReferenceEquals(_expected, args.ExceptionObject);
            var verified = matches && _producerAndStateVerified.Wait(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            if (matches && verified && args.IsTerminating)
            {
                WriteProbeLine(SuccessLine(Scenario, fatal: true));
            }
            else
            {
                var failure = new InvalidOperationException(
                    $"Unexpected unhandled error: terminating={args.IsTerminating}, identity={matches}, ownership={verified}, exception={args.ExceptionObject}");
                WriteProbeLine($"{FailureMarker}|scenario={Scenario}|exception={failure}");
                ChildCompletion.TrySetException(failure);
            }
            // Return to the real fatal path. The parent accepts its nonzero exit only when this
            // handler has verified the exact error, terminating status and producer-side state.
        }

        internal void ProducerReturnedAndOwnershipVerified()
        {
            _producerAndStateVerified.Set();
            // The real process-fatal report must arrive. Returning normally would hide a swallowed
            // exception; the handler verifies all producer-side ownership checks before reporting.
            using var neverSignaled = new ManualResetEventSlim();
            neverSignaled.Wait(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            throw new InvalidOperationException("The callback error was never reported asynchronously.");
        }
    }

    private sealed class PumpTaskScheduler : TaskScheduler, IDisposable
    {
        private readonly ConcurrentQueue<Task> _tasks = new();
        private readonly SemaphoreSlim _queued = new(0);

        protected override void QueueTask(Task task)
        {
            _tasks.Enqueue(task);
            _queued.Release();
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();

        internal Task RunOne()
        {
            Ensure(_queued.Wait(TimeSpan.FromSeconds(ProbeTimeoutSeconds)), "No scheduler callback was queued.");
            Ensure(_tasks.TryDequeue(out var task), "The scheduler queue was empty.");
            Ensure(TryExecuteTask(task!), "The scheduled task did not execute.");
            return task!;
        }

        public void Dispose() => _queued.Dispose();
    }

    private sealed class PumpSynchronizationContext(Exception? postError = null) : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private readonly SemaphoreSlim _queued = new(0);
        internal int PostCount { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            PostCount++;
            if (postError is not null)
                throw postError;
            _callbacks.Enqueue((callback, state));
            _queued.Release();
        }

        internal void RunOne()
        {
            Ensure(_queued.Wait(TimeSpan.FromSeconds(ProbeTimeoutSeconds)), "No context callback was queued.");
            Ensure(_callbacks.TryDequeue(out var callback), "The context queue was empty.");
            WithContext(this, () => callback.Callback(callback.State));
        }

        public void Dispose() => _queued.Dispose();
    }

    /// <summary>A single-consumer source which detaches its callback before allowing reentrancy.</summary>
    private sealed class ProbePipeReader : PipeReader, IValueTaskSource<ReadResult>
    {
        private Action<object?>? _continuation;
        private object? _state;
        private short _version;
        private bool _active;
        private bool _complete;
        private ReadResult _result;
        private Exception? _error;
        internal int ReadCount { get; private set; }
        internal int AdvanceCount { get; private set; }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            Ensure(!_active, "The inner source was rearmed before consumption.");
            ReadCount++;
            _active = true;
            _complete = false;
            _error = null;
            _result = default;
            _version = unchecked((short)(_version + 1));
            return new ValueTask<ReadResult>(this, _version);
        }

        internal void Publish(byte payload, Exception? error = null)
        {
            Ensure(_active && !_complete, "There is no pending inner read to publish.");
            _result = new ReadResult(new ReadOnlySequence<byte>(new[] { payload }), false, false);
            _error = error;
            _complete = true;
            var continuation = _continuation;
            var state = _state;
            _continuation = null;
            _state = null;
            Ensure(continuation is not null, "The wrapper must subscribe before publication.");
            continuation!(state);
        }

        public ReadResult GetResult(short token)
        {
            Ensure(_active && token == _version && _complete, "The inner result is not consumable.");
            _active = false;
            if (_error is not null)
                throw _error;
            return _result;
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            Ensure(_active && token == _version, "The inner source token is stale.");
            return !_complete ? ValueTaskSourceStatus.Pending
                : _error is null ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Faulted;
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            Ensure(_active && token == _version && !_complete && _continuation is null,
                "The inner source permits one pending registration.");
            _continuation = continuation;
            _state = state;
        }

        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => AdvanceCount++;
        public override void CancelPendingRead() { }
        public override void Complete(Exception? exception = null) { }
        public override bool TryRead(out ReadResult result) { result = default; return false; }
    }
}
