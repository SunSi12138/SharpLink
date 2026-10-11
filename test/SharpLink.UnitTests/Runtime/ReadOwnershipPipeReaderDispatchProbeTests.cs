using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading.Tasks.Sources;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Isolates dispatch/context fixtures in child processes. Contract probes retain their original
/// assertions; pooled characterizations explicitly document accepted Task compatibility differences.
/// No probe requires the old asynchronous process-fatal exception route or deep-recursion guard.
/// </summary>
[NotInParallel("read-ownership-dispatch-probes")]
public class ReadOwnershipPipeReaderDispatchProbeTests
{
    private const string ProbeEnvironmentVariable = "SHARPLINK_READ_OWNERSHIP_DISPATCH_PROBE";
    private const string SuccessMarker = "READ_OWNERSHIP_DISPATCH_PROBE_PASSED";
    private const string FailureMarker = "READ_OWNERSHIP_DISPATCH_PROBE_FAILED";
    private const int ProbeTimeoutSeconds = 15;
    private static int _childStarted;
    private static readonly TaskCompletionSource ChildCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // A: these three scenarios keep their original context-pump and backlog assertions.
    [Test]
    [Timeout(90_000)]
    [Arguments("context-before")]
    [Arguments("context-after")]
    [Arguments("late-backlog")]
    public Task DispatchShouldPreserveExceptionAndOwnershipSemantics(string scenario, CancellationToken cancellationToken)
        => RunScenario(scenario, cancellationToken);

    // C: all of these are valid single-consumer usages, not repeated-consumption misuse.
    // Passing these candidate characterizations does not claim old Task exception-route or
    // 100,000-level stack-guard equivalence. The old fatal probes must not be adoption gates.
    [Test]
    [Timeout(90_000)]
    [Arguments("raw-retained")]
    [Arguments("raw-rearmed")]
    [Arguments("fault-rearmed")]
    [Arguments("scheduler-before")]
    [Arguments("scheduler-after")]
    [Arguments("post-before")]
    [Arguments("post-after")]
    [Arguments("bounded-reentrancy")]
    [Arguments("caller-scheduled-long-chain")]
    public Task PooledDispatchCharacterizationShouldPreserveOwnership(string scenario, CancellationToken cancellationToken)
        => RunScenario(scenario, cancellationToken);

    private static async Task RunScenario(string scenario, CancellationToken cancellationToken)
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
        var expectedMarker = SuccessLine(scenario);
        Ensure(!timedOut && child.ExitCode == 0 &&
            ContainsLine(output, expectedMarker) && !output.Contains(FailureMarker, StringComparison.Ordinal),
            $"Dispatch probe '{scenario}' failed (timeout={timedOut}, exit={child.ExitCode}).\nstdout:\n{output}\nstderr:\n{errors}");
    }

    private static void RunDispatchProbe(string scenario)
    {
        try
        {
            switch (scenario)
            {
                case "raw-retained": RunRawThrow(rearm: false, faultRead: false); break;
                case "raw-rearmed": RunRawThrow(rearm: true, faultRead: false); break;
                case "fault-rearmed": RunRawThrow(rearm: true, faultRead: true); break;
                case "scheduler-before": RunSchedulerThrow(completeFirst: false); break;
                case "scheduler-after": RunSchedulerThrow(completeFirst: true); break;
                case "post-before": RunPostThrow(completeFirst: false); break;
                case "post-after": RunPostThrow(completeFirst: true); break;
                case "context-before": RunContextPumpThrow(completeFirst: false); break;
                case "context-after": RunContextPumpThrow(completeFirst: true); break;
                case "bounded-reentrancy": RunReentrantReads(targetReads: 32, queueNext: false); break;
                case "caller-scheduled-long-chain": RunReentrantReads(targetReads: 100_000, queueNext: true); break;
                case "late-backlog": RunLateNotificationBacklog(); break;
                default: throw new InvalidOperationException($"Unknown dispatch probe '{scenario}'.");
            }
            WriteProbeLine(SuccessLine(scenario));
            ChildCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            WriteProbeLine($"{FailureMarker}|scenario={scenario}|exception={exception}");
            ChildCompletion.TrySetException(exception);
        }
    }

    private static void RunRawThrow(bool rearm, bool faultRead)
    {
        var inner = new ProbePipeReader();
        var reader = new ReadOwnershipPipeReader(inner);
        var callbackError = new ApplicationException("raw callback marker");
        var readError = new IOException("inner fault marker");
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

        // Accepted C difference: unlike the previous Task boundary, a pending raw callback
        // can throw directly through the pooled builder into the producer. Catch it safely and
        // retain every original result/ownership/rearm assertion below.
        Ensure(ReferenceEquals(Capture(() => inner.Publish(0x41, faultRead ? readError : null)), callbackError),
            "The pending raw callback failure must be observable unchanged by this fixture's producer.");
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
    }

    private static void RunSchedulerThrow(bool completeFirst)
    {
        var inner = new ProbePipeReader();
        var reader = new ReadOwnershipPipeReader(inner);
        var failure = new ApplicationException("scheduled callback marker");
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
        // Accepted C difference: the BCL schedules the callback in a Task, which owns its
        // exception. The previous implementation completed that Task successfully and reported
        // the same error on an asynchronous fatal route. Observe this Task's fault explicitly.
        Ensure(callbackTask.IsFaulted && ReferenceEquals(Capture(() => callbackTask.GetAwaiter().GetResult()), failure),
            "The scheduler callback Task must retain the original callback failure.");
        Ensure(callbackCount == 1, "The scheduled callback must run exactly once.");
        VerifyNextRead(reader, inner);
    }

    private static void RunPostThrow(bool completeFirst)
    {
        var inner = new ProbePipeReader();
        var reader = new ReadOwnershipPipeReader(inner);
        var failure = new ApplicationException("SynchronizationContext.Post marker");
        var read = reader.ReadAsync();
        if (completeFirst)
            inner.Publish(0x41);
        using var context = new PumpSynchronizationContext(failure);
        var callbackCount = 0;
        // Accepted C difference: failing Post propagates to the registering caller for late
        // registration, or to the producer for a pending read. There is no Task containment
        // boundary that redirects the error to an asynchronous fatal route.
        var registrationError = Capture(() => WithContext(context,
            () => read.GetAwaiter().UnsafeOnCompleted(() => callbackCount++)));
        var publicationError = completeFirst ? null : Capture(() => inner.Publish(0x41));
        Ensure(completeFirst
                ? ReferenceEquals(registrationError, failure) && publicationError is null
                : registrationError is null && ReferenceEquals(publicationError, failure),
            "Post must fail at the candidate's registration/publication boundary with its original error.");
        Ensure(context.PostCount == 1 && callbackCount == 0,
            "Post must fail once without invoking the consumer callback.");
        Ensure(Capture(() => reader.ReadAsync()) is InvalidOperationException,
            "A scheduling error must not release the unread result's ownership.");
        Consume(reader, read, 0x41);
        VerifyNextRead(reader, inner);
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

    private static void RunReentrantReads(int targetReads, bool queueNext)
    {
        // Accepted C difference: the old 100,000-level synchronous recursion test used only
        // valid operations, but depended on Task's stack guard. Pooling does not promise it.
        // Check bounded nested rearming (32), and a long single-consumption chain (100,000)
        // with an explicit caller-owned scheduling boundary instead of risking stack overflow.
        var inner = new ProbePipeReader();
        var reader = new ReadOwnershipPipeReader(inner);
        using var finished = new ManualResetEventSlim();
        var completedReads = 0;
        Exception? error = null;
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
            if (queueNext)
                ThreadPool.UnsafeQueueUserWorkItem(_ => PublishSafely(), state: (object?)null, preferLocal: false);
            else
                inner.Publish(0x41);
        };
        var producer = new Thread(() =>
        {
            try
            {
                pending = reader.ReadAsync();
                pending.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(callback);
                PublishSafely();
            }
            catch (Exception exception)
            {
                error = exception;
                finished.Set();
            }
        }, 1024 * 1024)
        { IsBackground = true };
        producer.Start();
        Ensure(finished.Wait(TimeSpan.FromSeconds(ProbeTimeoutSeconds)),
            $"The reentrant chain stalled after {Volatile.Read(ref completedReads)} reads.");
        Ensure(producer.Join(TimeSpan.FromSeconds(ProbeTimeoutSeconds)), "The initial producer did not return.");
        Ensure(error is null, $"The reentrant chain failed: {error}");
        Ensure(completedReads == targetReads && inner.AdvanceCount == targetReads,
            "Every reentrant read must be consumed and advanced exactly once.");
        VerifyNextRead(reader, inner);

        void PublishSafely()
        {
            try { inner.Publish(0x41); }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref error, exception, null);
                finished.Set();
            }
        }
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

    private static string SuccessLine(string scenario)
        => $"{SuccessMarker}|scenario={scenario}|fatal=false";

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
