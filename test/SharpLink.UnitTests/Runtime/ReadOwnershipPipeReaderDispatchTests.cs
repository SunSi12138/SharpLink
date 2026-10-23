using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks.Sources;

namespace SharpLink.UnitTests.Runtime;

/// <summary>Single-consumption dispatch contracts and explicit pooled-builder compatibility differences.</summary>
public class ReadOwnershipPipeReaderDispatchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    // This is a whole-workload lifecycle guard, not a 10-second throughput assertion.
    // Per-callback progress and the final worker Join retain their original 10-second bounds.
    private static readonly TimeSpan QueuedWorkloadTimeout = TimeSpan.FromSeconds(30);
    // 30s workload + 10s worker Join + 10s fault-control drain + 10s reporting headroom.
    // Override only these tests; the assembly's ordinary 30-second policy is unchanged.
    private const int QueuedWorkloadParentTimeoutMilliseconds = 60_000;

    [Test]
    [Arguments(0, false)]
    [Arguments(0, true)]
    [Arguments(1, false)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    public async Task PublicAwaiterShouldPreserveCapturedStateAndOwnership(int mode, bool nullState)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new Thread(() =>
        {
            try
            {
                // B migration: use the public awaiter rather than the removed wrapper source/token.
                // Modes are pending inline, pending with a real queued context, and late queued.
                // The former reader policy switch and unsupported second registration are not tested.
                var inner = new ControlledReader();
                var reader = new ReadOwnershipPipeReader(inner);
                var read = reader.ReadAsync();
                var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var gate = (Lock)typeof(ReadOwnershipPipeReader).GetField("_gate", fields)!.GetValue(reader)!;
                object? expectedState = nullState ? null : new object();
                var calls = 0;
                var publishingThread = Environment.CurrentManagedThreadId;
                if (mode == 2) inner.Publish();
                var previousContext = SynchronizationContext.Current;
                try
                {
                    if (mode == 1) SynchronizationContext.SetSynchronizationContext(new QueuedSynchronizationContext());
                    Register(read, expectedState, state =>
                    {
                        try
                        {
                            Ensure(Interlocked.Increment(ref calls) == 1, "the callback must run exactly once");
                            Ensure((Environment.CurrentManagedThreadId == publishingThread) == (mode == 0),
                                "only the pending ordinary notification may run inline in this fixture");
                            Ensure(ReferenceEquals(state, expectedState), "the captured null or non-null state must survive dispatch");
                            Ensure(!gate.IsHeldByCurrentThread, "callbacks must run outside the ownership gate");
                            var result = read.GetAwaiter().GetResult();
                            Ensure(Capture(() => reader.ReadAsync()) is InvalidOperationException,
                                "observing a result must retain ownership until AdvanceTo");
                            reader.AdvanceTo(result.Buffer.End);
                            Ensure(inner.AdvanceCount == 1, "the single consumer must advance exactly once");
                            completed.SetResult();
                        }
                        catch (Exception error)
                        {
                            completed.TrySetException(error);
                        }
                    }, useSchedulingContext: mode == 1);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
                if (mode != 2) inner.Publish();
            }
            catch (Exception error)
            {
                completed.TrySetException(error);
            }
        })
        { IsBackground = true };
        publisher.Start();
        try
        {
            await completed.Task.WaitAsync(Timeout);
        }
        finally
        {
            Ensure(publisher.Join(Timeout), "the dedicated publisher must exit");
        }

        static void Register(ValueTask<ReadResult> read, object? state, Action<object?> continuation,
            bool useSchedulingContext)
            => read.ConfigureAwait(useSchedulingContext).GetAwaiter().UnsafeOnCompleted(() => continuation(state));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PooledDispatchCharacterizationConsumerChildrenCanAttachToPublishingTask(bool compilerAwait)
    {
        // Accepted C difference, including genuine compiler await: these are valid single-consumer
        // operations. Pooling does not preserve Task identity isolation or AttachedToParent isolation.
        // This records the candidate's behavior; it is not evidence of equivalence to the old Task.
        var inner = new ControlledReader();
        var reader = new ReadOwnershipPipeReader(inner);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseChild = new ManualResetEventSlim();
        var childFailure = new ApplicationException("consumer child");
        Task? child = null;
        int? callbackTask = null;
        var consumer = compilerAwait ? ConsumeAsync() : RegisterRaw();
        var producer = Task.Factory.StartNew(() =>
        {
            inner.Publish();
            producerReturned.SetResult();
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        try
        {
            // Awaiting the compiler consumer first can resume this test inline inside Publish,
            // which would prevent the producer delegate from returning while we inspect its status.
            // This asynchronous marker releases that stack before waiting for attached children.
            await producerReturned.Task.WaitAsync(Timeout);
            Ensure(callbackTask == producer.Id, "this pending pooled callback inherits the publishing Task identity");
            Ensure(SpinWait.SpinUntil(() => producer.Status == TaskStatus.WaitingForChildrenToComplete, Timeout),
                "the consumer's AttachedToParent child must hold the publishing Task open in this fixture");
        }
        finally
        {
            releaseChild.Set();
        }
        var producerError = await ObserveFailure(producer);
        var childError = await ObserveFailure(child!);
        await consumer.WaitAsync(Timeout);
        Ensure(ReferenceEquals(childError, childFailure), "the child must retain its original failure");
        Ensure(producerError is AggregateException aggregate &&
            aggregate.Flatten().InnerExceptions.Any(error => ReferenceEquals(error, childFailure)),
            "the attached child failure is also observable on the producer Task");
        Ensure(inner.AdvanceCount == 1, "Task attachment must not change read ownership release");
        var next = reader.ReadAsync();
        inner.Publish();
        reader.AdvanceTo(next.GetAwaiter().GetResult().Buffer.End);
        Ensure(inner.AdvanceCount == 2, "the next independent operation must remain consumable");

        Task RegisterRaw()
        {
            var read = reader.ReadAsync();
            read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => Consume(read.GetAwaiter().GetResult()));
            return observed.Task;
        }

        async Task ConsumeAsync() => Consume(await reader.ReadAsync().ConfigureAwait(false));

        void Consume(ReadResult result)
        {
            callbackTask = Task.CurrentId;
            reader.AdvanceTo(result.Buffer.End);
            child = Task.Factory.StartNew(() =>
            {
                Ensure(releaseChild.Wait(Timeout), "the child must be released");
                throw childFailure;
            }, CancellationToken.None, TaskCreationOptions.AttachedToParent, TaskScheduler.Default);
            observed.SetResult();
        }

        static async Task<Exception?> ObserveFailure(Task task)
        {
            try { await task.WaitAsync(Timeout); }
            catch (Exception error) { return error; }
            return null;
        }
    }

    [Test]
    [TUnit.Core.Timeout(QueuedWorkloadParentTimeoutMilliseconds)]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public Task QueuedPublicationOrLateRegistrationShouldDispatchExactlyOnce(
        bool late, bool useSchedulingContext, CancellationToken cancellationToken)
        => RunQueuedRegistration(late, useSchedulingContext, new QueuedRegistrationDiagnostics(),
            cancellationToken: cancellationToken);

    [Test]
    [TUnit.Core.Timeout(QueuedWorkloadParentTimeoutMilliseconds)]
    public async Task QueuedWorkloadLifecycleShouldAllowDelayedProgress(CancellationToken cancellationToken)
    {
        var diagnostics = new QueuedRegistrationDiagnostics();
        var control = new QueuedRegistrationControl(delayProgress: true);
        try
        {
            await RunQueuedRegistration(false, false, diagnostics, control, cancellationToken);
            Ensure(control.ProgressPauses == 2, "both between-iteration delays must execute");
            Ensure(diagnostics.CompletedIterations == 11_000 && diagnostics.CallbacksObserved == 11_000 &&
                diagnostics.AdvanceCountObserved == 11_000,
                "delayed progress must retain all 11,000 exactly-once operations");
            Ensure(System.Diagnostics.Stopwatch.GetElapsedTime(diagnostics.WorkerStarted,
                diagnostics.WorkerExited) > Timeout,
                "the control must exceed the former aggregate throughput bound");
            Ensure(diagnostics.WorkerTerminal == 1 && diagnostics.OuterOutcome == 1,
                "bounded delayed progress must complete successfully");
        }
        finally
        {
            control.ReleaseAndDrain();
        }
    }

    [Test]
    [TUnit.Core.Timeout(QueuedWorkloadParentTimeoutMilliseconds)]
    public async Task QueuedCallbackStallShouldKeepOriginalDeadlineAndDrain(CancellationToken cancellationToken)
    {
        var diagnostics = new QueuedRegistrationDiagnostics();
        var control = new QueuedRegistrationControl(withholdPublication: true);
        Exception? observed = null;
        try
        {
            await RunQueuedRegistration(false, false, diagnostics, control, cancellationToken);
        }
        catch (Exception error)
        {
            observed = error;
        }
        finally
        {
            // Also runs if an assertion, outer guard, or worker Join fails.
            control.ReleaseAndDrain();
        }
        Ensure(observed is InvalidOperationException &&
            observed.Message == "queued registration must dispatch without a lost wakeup",
            "an absent callback must fail the original per-callback deadline, not the outer lifecycle guard");
        Ensure(ReferenceEquals(observed, diagnostics.WorkerError) &&
            ReferenceEquals(observed, diagnostics.OuterError),
            "the original inner failure must survive worker completion and observation unchanged");
        Ensure(diagnostics.WorkerTerminal == 2 && diagnostics.WorkerExited != 0 &&
            diagnostics.JoinReturned >= diagnostics.WorkerExited,
            "the failing worker must be joined before this control returns");
        Ensure(diagnostics.CompletedIterations == 0 && diagnostics.CallbacksObserved == 1 &&
            diagnostics.AdvanceCountObserved == 1 && control.HeldCallbackDrained,
            "failure cleanup must publish and physically finish the held callback exactly once");
        // ManualResetEventSlim uses millisecond timeout accounting, not an exact Stopwatch minimum.
        Ensure(diagnostics.CallbackWaitTimeout == TimeSpan.FromSeconds(10),
            "the negative control must pass the unchanged 10-second timeout to the real callback wait");
    }

    private static async Task RunQueuedRegistration(bool late, bool useSchedulingContext,
        QueuedRegistrationDiagnostics diagnostics, QueuedRegistrationControl? control = null,
        CancellationToken cancellationToken = default)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registeringThread = new Thread(() =>
        {
            diagnostics.WorkerStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                ExerciseQueuedRegistration(late, useSchedulingContext, diagnostics, control);
                diagnostics.WorkerTerminal = 1;
                diagnostics.CompletionSetBefore = System.Diagnostics.Stopwatch.GetTimestamp();
                completed.SetResult();
                diagnostics.CompletionSetAfter = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            catch (Exception error)
            {
                diagnostics.WorkerError = error;
                diagnostics.WorkerTerminal = 2;
                diagnostics.CompletionSetBefore = System.Diagnostics.Stopwatch.GetTimestamp();
                completed.SetException(error);
                diagnostics.CompletionSetAfter = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            finally
            {
                diagnostics.WorkerExited = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        })
        { IsBackground = true };
        registeringThread.Start();
        try
        {
            diagnostics.OuterWaitEntered = System.Diagnostics.Stopwatch.GetTimestamp();
            await completed.Task.WaitAsync(QueuedWorkloadTimeout, cancellationToken);
            diagnostics.OuterOutcome = 1;
            diagnostics.OuterOutcomeObserved = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        catch (Exception error)
        {
            diagnostics.OuterOutcome = 2;
            diagnostics.OuterError = error;
            diagnostics.OuterOutcomeObserved = System.Diagnostics.Stopwatch.GetTimestamp();
            diagnostics.CompletionStatusAtOuterError = completed.Task.Status.ToString();
            diagnostics.IterationAtOuterError = Volatile.Read(ref diagnostics.CurrentIteration);
            diagnostics.CompletedIterationsAtOuterError = Volatile.Read(ref diagnostics.CompletedIterations);
            diagnostics.CallbacksAtOuterError = Volatile.Read(ref diagnostics.CallbacksObserved);
            throw;
        }
        finally
        {
            diagnostics.JoinEntered = System.Diagnostics.Stopwatch.GetTimestamp();
            var joined = registeringThread.Join(Timeout);
            diagnostics.JoinReturned = System.Diagnostics.Stopwatch.GetTimestamp();
            diagnostics.WriteAfterJoin(late, useSchedulingContext, completed.Task, joined);
            Ensure(joined, "the registering thread must exit");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BaseSynchronizationContextMustRemainAnOrdinaryRegistration(bool late)
    {
        var inner = new ControlledReader();
        var reader = new ReadOwnershipPipeReader(inner);
        var read = reader.ReadAsync();
        if (late) inner.Publish();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            // Plain await requests scheduling-context capture, but the exact base context is
            // a default, not a user dispatcher. Both pending and late ordinary cores ignore it.
            read.GetAwaiter().UnsafeOnCompleted(() =>
            {
                try
                {
                    Ensure(SynchronizationContext.Current is null,
                        "the base synchronization context must not be captured as a dispatcher");
                    var result = read.GetAwaiter().GetResult();
                    reader.AdvanceTo(result.Buffer.End);
                    completed.SetResult();
                }
                catch (Exception error)
                {
                    completed.SetException(error);
                }
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        if (!late) inner.Publish();
        await completed.Task.WaitAsync(Timeout);
    }

    // B policy-only tests are intentionally not carried forward: there is no mutable reader
    // dispatch switch, and public continuation registration does not classify context under the
    // reader's ownership gate. Context contracts are exercised above and in the lifetime tests.
    private static void ExerciseQueuedRegistration(bool late, bool useSchedulingContext,
        QueuedRegistrationDiagnostics diagnostics, QueuedRegistrationControl? control)
    {
        Ensure(Task.CurrentId is null && SynchronizationContext.Current is null,
            "the fixture requires an ordinary registering thread");
        var inner = new ControlledReader();
        var reader = new ReadOwnershipPipeReader(inner);
        var done = new ManualResetEventSlim();
        var callbackWaitTimeout = Timeout;
        diagnostics.CallbackWaitTimeout = callbackWaitTimeout;
        ValueTask<ReadResult> read = default;
        Exception? error = null;
        var consumed = 0;
        Action continuation = () =>
        {
            try
            {
                var result = read.GetAwaiter().GetResult();
                reader.AdvanceTo(result.Buffer.End);
                consumed++;
                Volatile.Write(ref diagnostics.CallbacksObserved, consumed);
                Volatile.Write(ref diagnostics.AdvanceCountObserved, inner.AdvanceCount);
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                done.Set();
                // The drain witness is published only AFTER the event is no longer touched.
                control?.CallbackFinished();
            }
        };
        // B/P migration: preserve the 11,000-operation exactly-once/no-lost-wakeup workload.
        // The old zero-allocation registering-thread policy is not a pooled correctness promise;
        // late OnCompleted may allocate its independent work item. Benchmark it separately.
        try
        {
            for (var i = 0; i < 11_000; i++)
            {
                control?.BeforeIteration(i, diagnostics);
                Volatile.Write(ref diagnostics.CurrentIteration, i + 1);
                done.Reset();
                read = reader.ReadAsync();
                if (late) inner.Publish();
                read.ConfigureAwait(useSchedulingContext).GetAwaiter().UnsafeOnCompleted(continuation);
                if (!late && !(control?.WithholdPublication(i, inner) ?? false))
                {
                    // Queue the fixture producer explicitly; no removed reader policy is simulated.
                    ThreadPool.UnsafeQueueUserWorkItem(_ =>
                    {
                        try { inner.Publish(); }
                        catch (Exception exception) { error = exception; done.Set(); }
                    }, state: (object?)null, preferLocal: false);
                }
                var waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var dispatched = done.Wait(callbackWaitTimeout);
                var waitTicks = System.Diagnostics.Stopwatch.GetTimestamp() - waitStarted;
                if (waitTicks > diagnostics.MaximumWaitTicks)
                {
                    diagnostics.MaximumWaitTicks = waitTicks;
                    diagnostics.MaximumWaitIteration = i + 1;
                }
                Ensure(dispatched, "queued registration must dispatch without a lost wakeup");
                Ensure(error is null, $"the queued consumer failed: {error}");
                Volatile.Write(ref diagnostics.CompletedIterations, i + 1);
            }
            Ensure(consumed == 11_000 && inner.AdvanceCount == 11_000,
                "every queued consumer must run and release ownership exactly once");
        }
        finally
        {
            if (control is null)
                done.Dispose();
            else
                control.ReleaseDrainAndDispose(done);
        }
    }

    private sealed class QueuedRegistrationControl(bool delayProgress = false, bool withholdPublication = false)
    {
        private ControlledReader? _heldReader;
        private readonly TaskCompletionSource _heldCallbackFinished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _publicationHeld;
        internal int ProgressPauses { get; private set; }
        internal bool HeldCallbackDrained { get; private set; }

        internal void BeforeIteration(int iteration, QueuedRegistrationDiagnostics diagnostics)
        {
            if (!delayProgress || (iteration != 1 && iteration != 5_501))
                return;
            Ensure(Task.CurrentId is null && !Thread.CurrentThread.IsThreadPoolThread &&
                SynchronizationContext.Current is null,
                "progress delays belong only to the dedicated registration thread");
            Ensure(diagnostics.CompletedIterations == iteration && diagnostics.CallbacksObserved == iteration &&
                diagnostics.AdvanceCountObserved == iteration,
                "a progress delay must begin only between fully consumed iterations");
            Thread.Sleep(TimeSpan.FromSeconds(6));
            ProgressPauses++;
        }

        internal bool WithholdPublication(int iteration, ControlledReader reader)
        {
            if (!withholdPublication || iteration != 0)
                return false;
            _heldReader = reader;
            Volatile.Write(ref _publicationHeld, 1);
            return true;
        }

        internal void CallbackFinished()
        {
            if (Volatile.Read(ref _publicationHeld) != 0)
                _heldCallbackFinished.TrySetResult();
        }

        internal void ReleaseDrainAndDispose(ManualResetEventSlim done)
        {
            try
            {
                ReleaseAndDrain();
            }
            finally
            {
                if (Volatile.Read(ref _publicationHeld) == 0 || _heldCallbackFinished.Task.IsCompleted)
                {
                    done.Dispose();
                }
                else
                {
                    // A failed bounded drain must not dispose an event a late callback can touch.
                    // Transfer disposal to the callback-end witness, without an unbounded wait.
                    _ = _heldCallbackFinished.Task.ContinueWith(static (_, state) =>
                        ((ManualResetEventSlim)state!).Dispose(), done, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }

        internal void ReleaseAndDrain()
        {
            Interlocked.Exchange(ref _heldReader, null)?.Publish();
            if (Volatile.Read(ref _publicationHeld) == 0)
                return;
            Ensure(_heldCallbackFinished.Task.Wait(Timeout),
                "the withheld callback must physically finish before its event is disposed");
            HeldCallbackDrained = true;
        }
    }

    // Durable terminal/progress evidence; diagnostics do not replace correctness assertions.
    private sealed class QueuedRegistrationDiagnostics
    {
        internal long WorkerStarted, WorkerExited, CompletionSetBefore, CompletionSetAfter;
        internal long OuterWaitEntered, OuterOutcomeObserved, JoinEntered, JoinReturned;
        internal long MaximumWaitTicks;
        internal TimeSpan CallbackWaitTimeout;
        internal int CurrentIteration, CompletedIterations, CallbacksObserved, AdvanceCountObserved;
        internal int MaximumWaitIteration, WorkerTerminal, OuterOutcome;
        internal int IterationAtOuterError, CompletedIterationsAtOuterError, CallbacksAtOuterError;
        internal Exception? WorkerError, OuterError;
        internal string? CompletionStatusAtOuterError;

        internal void WriteAfterJoin(bool late, bool useSchedulingContext, Task completed, bool joined)
        {
            // No output occurs in the measured iteration loop or on the worker/callback.
            // A diagnostic formatting failure must not replace the original test outcome.
            try
            {
                Console.WriteLine("QUEUED_DISPATCH_DIAGNOSTIC " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    late,
                    useSchedulingContext,
                    utcAfterJoin = DateTimeOffset.UtcNow.ToString("O"),
                    stopwatchFrequency = System.Diagnostics.Stopwatch.Frequency,
                    processorCount = Environment.ProcessorCount,
                    workerStarted = WorkerStarted,
                    workerExited = WorkerExited,
                    completionSetBefore = CompletionSetBefore,
                    completionSetAfter = CompletionSetAfter,
                    outerWaitEntered = OuterWaitEntered,
                    outerOutcomeObserved = OuterOutcomeObserved,
                    joinEntered = JoinEntered,
                    joinReturned = JoinReturned,
                    joined,
                    workerTerminal = WorkerTerminal,
                    outerOutcome = OuterOutcome,
                    completionStatusAfterJoin = completed.Status.ToString(),
                    completionStatusAtOuterError = CompletionStatusAtOuterError,
                    iteration = Volatile.Read(ref CurrentIteration),
                    completedIterations = Volatile.Read(ref CompletedIterations),
                    callbacksObserved = Volatile.Read(ref CallbacksObserved),
                    advanceCountObserved = Volatile.Read(ref AdvanceCountObserved),
                    iterationAtOuterError = IterationAtOuterError,
                    completedIterationsAtOuterError = CompletedIterationsAtOuterError,
                    callbacksAtOuterError = CallbacksAtOuterError,
                    callbackWaitTimeout = CallbackWaitTimeout,
                    maximumWaitTicks = MaximumWaitTicks,
                    maximumWaitIteration = MaximumWaitIteration,
                    workerError = WorkerError?.ToString(),
                    outerError = OuterError?.ToString()
                }));
            }
            catch
            {
                // Diagnostics are not a new correctness assertion or a failure suppression path.
            }
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
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
            => ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), state: (object?)null, preferLocal: false);
    }

    private sealed class ControlledReader : PipeReader, IValueTaskSource<ReadResult>
    {
        private Action<object?>? _callback;
        private object? _state;
        private short _version;
        private bool _completed;
        internal int AdvanceCount { get; private set; }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            _completed = false;
            return new ValueTask<ReadResult>(this, unchecked(++_version));
        }

        internal void Publish()
        {
            _completed = true;
            var callback = _callback;
            var state = _state;
            _callback = null;
            _state = null;
            callback!(state);
        }

        public ReadResult GetResult(short token)
        {
            Ensure(token == _version && _completed, "the inner read must complete before observation");
            return new ReadResult(default, false, false);
        }

        public ValueTaskSourceStatus GetStatus(short token)
            => _completed ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _callback = continuation;
            _state = state;
        }

        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => AdvanceCount++;
        public override void CancelPendingRead() { }
        public override void Complete(Exception? exception = null) { }
        public override bool TryRead(out ReadResult result) { result = default; return false; }
    }
}
