using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks.Sources;

namespace SharpLink.UnitTests.Runtime;

/// <summary>Single-consumption dispatch contracts and explicit pooled-builder compatibility differences.</summary>
public class ReadOwnershipPipeReaderDispatchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task QueuedPublicationOrLateRegistrationShouldDispatchExactlyOnce(
        bool late, bool useSchedulingContext)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registeringThread = new Thread(() =>
        {
            try
            {
                ExerciseQueuedRegistration(late, useSchedulingContext);
                completed.SetResult();
            }
            catch (Exception error)
            {
                completed.SetException(error);
            }
        })
        { IsBackground = true };
        registeringThread.Start();
        try
        {
            await completed.Task.WaitAsync(Timeout);
        }
        finally
        {
            Ensure(registeringThread.Join(Timeout), "the registering thread must exit");
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
    private static void ExerciseQueuedRegistration(bool late, bool useSchedulingContext)
    {
        Ensure(Task.CurrentId is null && SynchronizationContext.Current is null,
            "the fixture requires an ordinary registering thread");
        var inner = new ControlledReader();
        var reader = new ReadOwnershipPipeReader(inner);
        using var done = new ManualResetEventSlim();
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
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                done.Set();
            }
        };
        // B/P migration: preserve the 11,000-operation exactly-once/no-lost-wakeup workload.
        // The old zero-allocation registering-thread policy is not a pooled correctness promise;
        // late OnCompleted may allocate its independent work item. Benchmark it separately.
        for (var i = 0; i < 11_000; i++)
        {
            done.Reset();
            read = reader.ReadAsync();
            if (late) inner.Publish();
            read.ConfigureAwait(useSchedulingContext).GetAwaiter().UnsafeOnCompleted(continuation);
            if (!late)
            {
                // Queue the fixture producer explicitly; no removed reader policy is simulated.
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    try { inner.Publish(); }
                    catch (Exception exception) { error = exception; done.Set(); }
                }, state: (object?)null, preferLocal: false);
            }
            Ensure(done.Wait(Timeout), "queued registration must dispatch without a lost wakeup");
            Ensure(error is null, $"the queued consumer failed: {error}");
        }
        Ensure(consumed == 11_000 && inner.AdvanceCount == 11_000,
            "every queued consumer must run and release ownership exactly once");
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
