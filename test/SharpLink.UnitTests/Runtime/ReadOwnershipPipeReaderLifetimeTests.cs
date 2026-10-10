using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace SharpLink.UnitTests.Runtime;

/// <summary>Consumed state and continuation contexts must not become per-connection roots.</summary>
public class ReadOwnershipPipeReaderLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public void ConsumedSuccessShouldNotRetainPayload(bool inline, bool synchronousReads)
    {
        var (reader, payload) = CompleteSuccess(inline, synchronousReads);
        Collect();
        Ensure(!payload.IsAlive, "an idle or sync-only reader must not retain the consumed buffer");
        GC.KeepAlive(reader);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public void ConsumedFaultShouldNotRetainException(bool inline, bool synchronousReads)
    {
        var (reader, error) = CompleteFault(inline, synchronousReads);
        Collect();
        Ensure(!error.IsAlive, "an idle or sync-only reader must not retain the consumed exception/EDI");
        GC.KeepAlive(reader);
    }

    [Test]
    public void ObservedSuccessShouldDropPayloadBeforeAdvanceTo()
    {
        var (reader, payload) = ObserveWithoutAdvancing();
        Collect();
        Ensure(!payload.IsAlive, "single observation must drop the source payload without retaining it for another GetResult");
        var rejected = false;
        try
        {
            reader.ReadAsync();
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Ensure(rejected, "clearing consumed source state must not release transport buffer ownership");
        reader.AdvanceTo(default);
        GC.KeepAlive(reader);
    }

    [Test]
    public void ConsumedInlineContinuationShouldNotRetainCapturedState()
    {
        var (reader, state) = CompleteCapturingContinuation();
        Collect();
        Ensure(!state.IsAlive, "the reader must drop a dispatched continuation and its captured state");
        GC.KeepAlive(reader);
    }

    [Test]
    public void ConsumedContinuationShouldNotRetainExecutionContext()
    {
        var (reader, state) = CompleteFlowingContinuation();
        Collect();
        Ensure(!state.IsAlive, "the reader must drop the dispatched ExecutionContext and its AsyncLocal state");
        GC.KeepAlive(reader);
    }

    [Test]
    public void ConsumedReadShouldNotRetainReadCallerExecutionContext()
    {
        var (reader, state) = CompleteReadCallerContext();
        Collect();
        Ensure(!state.IsAlive, "the reader must drop the read caller's captured ExecutionContext after publication");
        GC.KeepAlive(reader);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SafeContinuationShouldRestoreExecutionContext(bool completedBeforeRegistration)
    {
        var local = new AsyncLocal<string?>();
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var read = reader.ReadAsync();
        var observed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completedBeforeRegistration)
            fake.Publish(EmptyResult());

        local.Value = "subscriber";
        read.ConfigureAwait(false).GetAwaiter().OnCompleted(() =>
        {
            var result = read.GetAwaiter().GetResult();
            reader.AdvanceTo(result.Buffer.End);
            observed.SetResult(local.Value);
            local.Value = "consumer mutation";
        });
        local.Value = "publisher";
        if (!completedBeforeRegistration)
            fake.Publish(EmptyResult());
        Ensure(local.Value == "publisher", "dispatch must restore the publisher's ExecutionContext");
        Ensure(await observed.Task.WaitAsync(Timeout).ConfigureAwait(false) == "subscriber",
            "safe registration must flow the subscriber's AsyncLocal value");
        local.Value = null;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void UnsafeContinuationShouldRunUnderReadCallerContextLikeTheAsyncWrapper(bool suppressFlow)
    {
        var local = new AsyncLocal<string?>();
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        local.Value = "read caller";
        ValueTask<ReadResult> read;
        if (suppressFlow)
        {
            using (ExecutionContext.SuppressFlow())
                read = reader.ReadAsync();
        }
        else
        {
            read = reader.ReadAsync();
        }
        string? observed = null;
        local.Value = "subscriber";
        read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
        {
            observed = local.Value;
            var result = read.GetAwaiter().GetResult();
            reader.AdvanceTo(result.Buffer.End);
        });
        local.Value = "publisher";
        fake.Publish(EmptyResult());
        Ensure(observed == (suppressFlow ? "publisher" : "read caller"),
            "unsafe registration must preserve the async wrapper's read-caller capture and suppression policy");
        Ensure(local.Value == "publisher", "forwarding must restore the publisher's context afterward");
        local.Value = null;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AwaitShouldRestoreAsyncLocalAcrossSuspension(bool configureAwaitFalse)
    {
        var local = new AsyncLocal<string?>();
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        local.Value = "consumer";
        var consumer = Consume();
        local.Value = "producer";
        fake.Publish(EmptyResult());
        Ensure(await consumer.WaitAsync(Timeout).ConfigureAwait(false) == "consumer",
            "compiler await must preserve ExecutionContext with either ConfigureAwait policy");
        Ensure(local.Value == "producer", "consumer context changes must not escape to the producer");
        local.Value = null;

        async Task<string?> Consume()
        {
            var read = reader.ReadAsync();
            var result = configureAwaitFalse ? await read.ConfigureAwait(false) : await read;
            reader.AdvanceTo(result.Buffer.End);
            var value = local.Value;
            local.Value = "consumer mutation";
            return value;
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ContinuationShouldHonorSynchronizationContext(bool completedBeforeRegistration, bool useContext)
    {
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var read = reader.ReadAsync();
        if (completedBeforeRegistration)
            fake.Publish(EmptyResult());
        var context = new QueuedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var observed = new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            read.ConfigureAwait(useContext).GetAwaiter().UnsafeOnCompleted(() =>
            {
                var result = read.GetAwaiter().GetResult();
                reader.AdvanceTo(result.Buffer.End);
                observed.SetResult(SynchronizationContext.Current);
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await Task.Run(() =>
        {
            if (!completedBeforeRegistration)
                fake.Publish(EmptyResult());
            if (useContext)
            {
                Ensure(!observed.Task.IsCompleted, "a captured context must own dispatch until pumped");
                context.RunOne();
            }
        }).WaitAsync(Timeout).ConfigureAwait(false);
        var actual = await observed.Task.WaitAsync(Timeout).ConfigureAwait(false);
        Ensure(useContext ? ReferenceEquals(actual, context) : !ReferenceEquals(actual, context),
            "UseSchedulingContext must select the captured context; ConfigureAwait(false) must bypass it");
        Ensure(context.PostCount == (useContext ? 1 : 0), "context must receive exactly the requested dispatch");
    }

    [Test]
    public async Task ReadEntrySynchronizationContextMustNotOwnInnerForwarding()
    {
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var context = new QueuedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        ValueTask<ReadResult> read;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            read = reader.ReadAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
        {
            ConsumeSuccess(reader, read);
            observed.SetResult();
        });
        fake.Publish(EmptyResult());
        Ensure(context.PostCount == 0, "inner forwarding must not capture the read entry context");
        await observed.Task.WaitAsync(Timeout).ConfigureAwait(false);
    }

    [Test]
    public async Task ReadEntryTaskSchedulerMustNotOwnInnerForwarding()
    {
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var scheduler = new QueuedTaskScheduler();
        var start = Task.Factory.StartNew(() => reader.ReadAsync(), CancellationToken.None,
            TaskCreationOptions.None, scheduler);
        scheduler.RunOne();
        var read = start.GetAwaiter().GetResult();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
        {
            ConsumeSuccess(reader, read);
            observed.SetResult();
        });
        fake.Publish(EmptyResult());
        Ensure(scheduler.QueueCount == 1, "inner forwarding must not capture the read entry scheduler");
        await observed.Task.WaitAsync(Timeout).ConfigureAwait(false);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ContinuationShouldHonorTaskScheduler(bool completedBeforeRegistration, bool useScheduler)
    {
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var read = reader.ReadAsync();
        if (completedBeforeRegistration)
            fake.Publish(EmptyResult());
        var scheduler = new QueuedTaskScheduler();
        var observed = new TaskCompletionSource<TaskScheduler>(TaskCreationOptions.RunContinuationsAsynchronously);
        var register = Task.Factory.StartNew(() =>
        {
            read.ConfigureAwait(useScheduler).GetAwaiter().UnsafeOnCompleted(() =>
            {
                ConsumeSuccess(reader, read);
                observed.SetResult(TaskScheduler.Current);
            });
        }, CancellationToken.None, TaskCreationOptions.None, scheduler);
        scheduler.RunOne();
        register.GetAwaiter().GetResult();
        if (!completedBeforeRegistration)
            fake.Publish(EmptyResult());
        if (useScheduler)
        {
            Ensure(!observed.Task.IsCompleted, "the consumer's scheduler must own its requested dispatch");
            scheduler.RunOne();
        }
        var actual = await observed.Task.WaitAsync(Timeout).ConfigureAwait(false);
        Ensure(useScheduler ? ReferenceEquals(actual, scheduler) : !ReferenceEquals(actual, scheduler),
            "consumer scheduling preference must be preserved independently of inner forwarding");
        Ensure(scheduler.QueueCount == (useScheduler ? 2 : 1), "the scheduler must receive only requested work");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompletionRacingRegistrationShouldDispatchExactlyOnce(bool useContext)
    {
        for (var i = 0; i < 500; i++)
        {
            var fake = new EphemeralPipeReader();
            var reader = new ReadOwnershipPipeReader(fake);
            var read = reader.ReadAsync();
            using var barrier = new Barrier(2);
            var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var publication = Task.Run(() =>
            {
                Ensure(barrier.SignalAndWait(Timeout), "registration must reach the barrier");
                fake.Publish(EmptyResult());
            });
            Ensure(barrier.SignalAndWait(Timeout), "publication must reach the barrier");
            // The candidate has no reader dispatch-policy switch. Exercise the natural
            // publication/registration race and an explicitly queued consumer context instead.
            var context = new QueuedSynchronizationContext();
            var previous = SynchronizationContext.Current;
            try
            {
                if (useContext)
                    SynchronizationContext.SetSynchronizationContext(context);
                read.ConfigureAwait(useContext).GetAwaiter().UnsafeOnCompleted(() =>
                {
                    Interlocked.Increment(ref calls);
                    ConsumeSuccess(reader, read);
                    dispatched.SetResult();
                });
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
            await publication.WaitAsync(Timeout).ConfigureAwait(false);
            if (useContext)
            {
                Ensure(!dispatched.Task.IsCompleted, "the captured context must own dispatch until pumped");
                context.RunOne();
                Ensure(context.PostCount == 1, "the queued consumer must be dispatched exactly once");
            }
            await dispatched.Task.WaitAsync(Timeout).ConfigureAwait(false);
            Ensure(calls == 1, "racing registration/publication must dispatch exactly once");
        }
    }

    [Test]
    public void NestedInlineRearmingShouldLeaveNewestArmIntact()
    {
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var first = reader.ReadAsync();
        ValueTask<ReadResult> third = default;
        var calls = 0;
        first.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
        {
            ConsumeSuccess(reader, first);
            calls++;
            var second = reader.ReadAsync();
            second.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
            {
                ConsumeSuccess(reader, second);
                calls++;
                third = reader.ReadAsync();
            });
            fake.Publish(EmptyResult());
            Ensure(!third.IsCompleted, "the nested publication must leave the third arm pending");
        });
        fake.Publish(EmptyResult());
        Ensure(calls == 2, "both nested inline callbacks must run exactly once");
        Ensure(!third.IsCompleted, "unwinding older dispatches must not reset or complete a newer arm");
        fake.Publish(EmptyResult());
        ConsumeSuccess(reader, third);
    }

    [Test]
    public void FaultExceptionFilterShouldRunOutsideReadGate()
    {
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var gate = (Lock)typeof(ReadOwnershipPipeReader).GetField("_gate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reader)!;
        var read = reader.ReadAsync();
        fake.PublishFault(new IOException("filter probe"));
        bool? heldInFilter = null;
        try
        {
            read.GetAwaiter().GetResult();
        }
        catch (IOException) when (ObserveFilter())
        {
        }
        Ensure(heldInFilter == false,
            "a caller exception filter must not run under the read gate before its finally unwinds");
        var next = reader.ReadAsync();
        fake.Publish(EmptyResult());
        ConsumeSuccess(reader, next);

        bool ObserveFilter()
        {
            heldInFilter = gate.IsHeldByCurrentThread;
            return true;
        }
    }

    [Test]
    public void SuccessfulSuspensionsShouldNotAllocateAfterWarmup()
    {
        // A single reader, warm same-thread cache, and one outstanding box. This exact zero is
        // a controlled allocation regression, not a promise for bursts or cross-thread reads.
        var fake = new AllocationFreePipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        for (var i = 0; i < 1_000; i++)
            Round();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            Round();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Ensure(allocated == 0, $"successful suspended read/release must allocate 0 bytes, got {allocated}");

        void Round()
        {
            var read = reader.ReadAsync();
            fake.Publish();
            reader.AdvanceTo(read.GetAwaiter().GetResult().Buffer.End);
        }
    }

    [Test]
    public void CurrentOperationStatusShouldRemainCorrectAcrossManyPoolReuses()
    {
        var fake = new AllocationFreePipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        for (var i = 0; i < ushort.MaxValue + 3; i++)
        {
            var current = reader.ReadAsync();
            Ensure(!current.IsCompleted, "each new operation must initially report Pending");
            fake.Publish();
            Ensure(current.IsCompletedSuccessfully, "the live operation must report its own completed status");
            var result = current.GetAwaiter().GetResult();
            reader.AdvanceTo(result.Buffer.End);
            // Never query the consumed ValueTask: pooled token versioning is an implementation
            // detail, and stale-status/repeated-GetResult rejection is not a consumer guarantee.
        }
    }

    [Test]
    public void RegisteredConsumerSuspensionsShouldNotAllocateAfterWarmup()
    {
        // Only this warm single-thread fixture with a cached callback is required to allocate
        // zero. Late queued registration and multiple simultaneously rented boxes may allocate.
        var fake = new AllocationFreePipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        ValueTask<ReadResult> current = default;
        var consumed = 0;
        Action continuation = () =>
        {
            var result = current.GetAwaiter().GetResult();
            reader.AdvanceTo(result.Buffer.End);
            consumed++;
        };
        for (var i = 0; i < 1_000; i++)
            Round();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            Round();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Ensure(consumed == 11_000, "every pending read must invoke its registered consumer exactly once");
        Ensure(allocated == 0, $"suspended reads with a cached consumer must allocate 0 bytes, got {allocated}");

        void Round()
        {
            current = reader.ReadAsync();
            Ensure(!current.IsCompleted, "the consumer must register before inner completion");
            current.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(continuation);
            fake.Publish();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ReadOwnershipPipeReader Reader, WeakReference Payload) CompleteSuccess(bool inline, bool synchronousReads)
    {
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var payload = new byte[64 * 1024];
        var weak = new WeakReference(payload);
        var read = reader.ReadAsync();
        if (inline)
            read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => ConsumeSuccess(reader, read));
        fake.Publish(new ReadResult(new ReadOnlySequence<byte>(payload), false, false));
        if (!inline)
            ConsumeSuccess(reader, read);
        if (synchronousReads)
            RunSynchronousReads(reader, fake);
        return (reader, weak);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ReadOwnershipPipeReader Reader, WeakReference Payload) ObserveWithoutAdvancing()
    {
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var payload = new byte[64 * 1024];
        var weak = new WeakReference(payload);
        var read = reader.ReadAsync();
        fake.Publish(new ReadResult(new ReadOnlySequence<byte>(payload), false, false));
        _ = read.GetAwaiter().GetResult();
        return (reader, weak);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ReadOwnershipPipeReader Reader, WeakReference Error) CompleteFault(bool inline, bool synchronousReads)
    {
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var error = new IOException("retention marker");
        var weak = new WeakReference(error);
        var read = reader.ReadAsync();
        if (inline)
            read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => ConsumeFault(read));
        fake.PublishFault(error);
        if (!inline)
            ConsumeFault(read);
        if (synchronousReads)
            RunSynchronousReads(reader, fake);
        return (reader, weak);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ReadOwnershipPipeReader Reader, WeakReference State) CompleteCapturingContinuation()
    {
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var state = new object();
        var weak = new WeakReference(state);
        var read = reader.ReadAsync();
        read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
        {
            ConsumeSuccess(reader, read);
            GC.KeepAlive(state);
        });
        fake.Publish(EmptyResult());
        return (reader, weak);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ReadOwnershipPipeReader Reader, WeakReference State) CompleteFlowingContinuation()
    {
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var local = new AsyncLocal<object?>();
        var state = new object();
        var weak = new WeakReference(state);
        var read = reader.ReadAsync();
        local.Value = state;
        read.ConfigureAwait(false).GetAwaiter().OnCompleted(() =>
        {
            Ensure(local.Value is not null, "the captured context must be restored while dispatching");
            ConsumeSuccess(reader, read);
        });
        local.Value = null;
        fake.Publish(EmptyResult());
        return (reader, weak);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ReadOwnershipPipeReader Reader, WeakReference State) CompleteReadCallerContext()
    {
        var fake = new EphemeralPipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var local = new AsyncLocal<object?>();
        var state = new object();
        var weak = new WeakReference(state);
        local.Value = state;
        var read = reader.ReadAsync();
        local.Value = null;
        fake.Publish(EmptyResult());
        ConsumeSuccess(reader, read);
        return (reader, weak);
    }

    private static void ConsumeSuccess(ReadOwnershipPipeReader reader, ValueTask<ReadResult> read)
        => reader.AdvanceTo(read.GetAwaiter().GetResult().Buffer.End);

    private static void ConsumeFault(ValueTask<ReadResult> read)
    {
        try
        {
            read.GetAwaiter().GetResult();
            throw new InvalidOperationException("the fault must be observed");
        }
        catch (IOException)
        {
        }
    }

    private static void RunSynchronousReads(ReadOwnershipPipeReader reader, EphemeralPipeReader fake)
    {
        fake.Synchronous = true;
        for (var i = 0; i < 3; i++)
            ConsumeSuccess(reader, reader.ReadAsync());
    }

    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static ReadResult EmptyResult() => new(ReadOnlySequence<byte>.Empty, false, false);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    // Unlike a reusable fake core, this reader drops its task before dispatch. It cannot itself
    // retain the payload/exception under test once the no-inline producer helper returns.
    private sealed class EphemeralPipeReader : PipeReader
    {
        private TaskCompletionSource<ReadResult>? _pending;
        internal bool Synchronous { get; set; }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (Synchronous)
                return new ValueTask<ReadResult>(EmptyResult());
            _pending = new TaskCompletionSource<ReadResult>();
            return new ValueTask<ReadResult>(_pending.Task);
        }

        internal void Publish(ReadResult result) => TakePending().SetResult(result);
        internal void PublishFault(Exception error) => TakePending().SetException(error);
        private TaskCompletionSource<ReadResult> TakePending()
        {
            var pending = _pending!;
            _pending = null;
            return pending;
        }

        public override void AdvanceTo(SequencePosition consumed) { }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) { }
        public override void CancelPendingRead() { }
        public override void Complete(Exception? exception = null) { }
        public override bool TryRead(out ReadResult result) { result = default; return false; }
    }

    private sealed class AllocationFreePipeReader : PipeReader, IValueTaskSource<ReadResult>
    {
        private ManualResetValueTaskSourceCore<ReadResult> _source;
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            _source.Reset();
            return new ValueTask<ReadResult>(this, _source.Version);
        }
        internal void Publish() => _source.SetResult(EmptyResult());
        public override void AdvanceTo(SequencePosition consumed) { }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) { }
        public override void CancelPendingRead() { }
        public override void Complete(Exception? exception = null) { }
        public override bool TryRead(out ReadResult result) { result = default; return false; }
        public ReadResult GetResult(short token) => _source.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _source.OnCompleted(continuation, state, token, flags);
    }

    private sealed class QueuedTaskScheduler : TaskScheduler
    {
        private readonly BlockingCollection<Task> _queue = new();
        private int _queueCount;
        internal int QueueCount => Volatile.Read(ref _queueCount);
        protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref _queueCount);
            _queue.Add(task);
        }
        internal void RunOne()
        {
            Ensure(_queue.TryTake(out var task, Timeout), "the scheduler must receive the requested work");
            Ensure(TryExecuteTask(task!), "the queued task must execute");
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        internal int PostCount { get; private set; }
        public override void Post(SendOrPostCallback d, object? state)
        {
            PostCount++;
            _queue.Add((d, state));
        }
        internal void RunOne()
        {
            Ensure(_queue.TryTake(out var work, Timeout), "the continuation must post to its context");
            var previous = Current;
            try
            {
                SetSynchronizationContext(this);
                work.Callback(work.State);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
