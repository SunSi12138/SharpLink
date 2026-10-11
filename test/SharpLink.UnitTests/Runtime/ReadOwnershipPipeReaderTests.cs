using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Single-consumption ValueTask results, continuation delivery, and transport buffer ownership.
/// Each suspended operation has its own BCL pooled state; consumed operations are never observed
/// again, and an unobserved failure must not corrupt a newer operation.
/// </summary>
public class ReadOwnershipPipeReaderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // ========================================================================================
    // 1. Single-consumption ValueTask protocol
    // ========================================================================================

    [Test]
    public async Task StatusShouldBePendingBeforeCompletionAndSucceededAfter()
    {
        var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
        var reader = new ReadOwnershipPipeReader(fake);

        var read = reader.ReadAsync();
        Ensure(!read.IsCompleted, "a suspended read must report incomplete before the inner publishes");

        fake.Publish(Result(0x11));

        Ensure(read.IsCompletedSuccessfully, "the read must report successful completion after publishing");
        var result = await WithTimeout(read, "suspended read");
        Ensure(result.Buffer.ToArray()[0] == 0x11, "the published payload must be delivered");
        reader.AdvanceTo(result.Buffer.End);
    }

    [Test]
    public async Task FaultedReadShouldPropagateTheSameExceptionInstance()
    {
        var failure = new IOException("inner read faulted");
        var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
        var reader = new ReadOwnershipPipeReader(fake);

        var read = reader.ReadAsync();
        fake.PublishFault(failure);

        var observed = await CaptureAsync(async () => await WithTimeout(read, "faulted read"));
        Ensure(ReferenceEquals(observed, failure),
            "the faulted read must surface the exact exception instance the inner produced");
        Ensure(fake.OutstandingArms == 0, "a faulted suspension must not leave an armed inner read");
    }

    [Test]
    public async Task AwaiterRegisteredBeforeCompletionShouldRunExactlyOnce()
    {
        var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
        var reader = new ReadOwnershipPipeReader(fake);

        var continuations = 0;
        var completion = new TaskCompletionSource<ReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var read = reader.ReadAsync();
        // Registering OnCompleted is what a normal `await` does; drive it explicitly so the count is
        // observable.
        read.GetAwaiter().UnsafeOnCompleted(() =>
        {
            Interlocked.Increment(ref continuations);
            completion.TrySetResult(read.GetAwaiter().GetResult());
        });

        fake.Publish(Result(0x33));
        var result = await WithTimeout(new ValueTask<ReadResult>(completion.Task), "oncompleted continuation");

        Ensure(Volatile.Read(ref continuations) == 1,
            $"a continuation registered before completion must run exactly once (ran {continuations})");
        Ensure(result.Buffer.ToArray()[0] == 0x33, "the continuation must receive the published result");
        reader.AdvanceTo(result.Buffer.End);
    }

    [Test]
    public async Task AwaiterRegisteredAfterCompletionShouldStillRun()
    {
        // This is the classic manual-source hang: if OnCompleted only arms a continuation when the
        // source is still pending, a continuation registered afterwards is silently dropped and the
        // awaiter never completes.
        var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
        var reader = new ReadOwnershipPipeReader(fake);

        var read = reader.ReadAsync();
        fake.Publish(Result(0x44)); // completed BEFORE anything subscribes

        var ran = 0;
        var completion = new TaskCompletionSource<ReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        read.GetAwaiter().UnsafeOnCompleted(() =>
        {
            Interlocked.Increment(ref ran);
            completion.TrySetResult(read.GetAwaiter().GetResult());
        });

        var result = await WithTimeout(new ValueTask<ReadResult>(completion.Task),
            "continuation registered after completion");
        Ensure(Volatile.Read(ref ran) == 1, "a continuation registered after completion must still run exactly once");
        Ensure(result.Buffer.ToArray()[0] == 0x44, "it must observe the already-published result");
        reader.AdvanceTo(result.Buffer.End);
    }

    [Test]
    public void SuccessiveOperationsMustDeliverTheirOwnResults()
    {
        // A consumed pooled ValueTask must not be queried again, even just for status. Preserve
        // operation isolation using each result once rather than testing invalid stale tokens.
        var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
        var reader = new ReadOwnershipPipeReader(fake);
        var first = reader.ReadAsync();
        fake.Publish(Result(0x55));
        var firstResult = first.GetAwaiter().GetResult();
        Ensure(firstResult.Buffer.ToArray()[0] == 0x55, "the first operation must receive its own payload");
        reader.AdvanceTo(firstResult.Buffer.End);

        var second = reader.ReadAsync();
        Ensure(!second.IsCompleted, "the next operation must initially be pending");
        fake.Publish(Result(0x66));
        var secondResult = second.GetAwaiter().GetResult();
        Ensure(secondResult.Buffer.ToArray()[0] == 0x66, "the next operation must receive its own payload");
        reader.AdvanceTo(secondResult.Buffer.End);
    }

    // ========================================================================================
    // 2. Documented reentrancy windows
    // ========================================================================================

    [Test]
    public async Task InnerCompletingInlineDuringSubscribeShouldDeliverExactlyOnce()
    {
        // ReadAsync checks IsCompletedSuccessfully, then registers a continuation. An inner read can
        // complete in that window, which invokes the wrapper's completion callback inline - before
        // ReadAsync has returned its ValueTask.
        var fake = new FakePipeReader { Mode = FakeReadMode.CompleteOnSubscribe };
        var reader = new ReadOwnershipPipeReader(fake);

        var read = reader.ReadAsync();

        Ensure(read.IsCompletedSuccessfully,
            "an inner read that completed inline during subscription must yield an already-completed ValueTask");
        var result = read.GetAwaiter().GetResult();
        Ensure(result.Buffer.ToArray()[0] == 0x77, "the inline-completed result must be delivered");
        reader.AdvanceTo(result.Buffer.End);
        Ensure(fake.OutstandingArms == 0, "the inline completion must not leave an armed inner read");
    }

    [Test]
    public async Task InlineCompletedReadShouldBeImmediatelyObservableAndIndependentOfTheNextArm()
    {
        // When the inner read completes inline during subscription, ReadAsync has already published
        // the result by the time it returns, so the returned ValueTask is complete immediately and a
        // plain `await` never suspends. The next arm must then be fully independent of it.
        var fake = new FakePipeReader { Mode = FakeReadMode.CompleteOnSubscribe };
        var reader = new ReadOwnershipPipeReader(fake);

        var first = reader.ReadAsync();
        Ensure(first.IsCompletedSuccessfully,
            "an inline-completed inner read must yield an already-completed ValueTask");
        var firstResult = first.GetAwaiter().GetResult();
        Ensure(firstResult.Buffer.ToArray()[0] == 0x77, "arm 1 must deliver its own payload");
        reader.AdvanceTo(firstResult.Buffer.End);
        Ensure(fake.OutstandingArms == 0, "the inline completion must not leave an armed inner read");

        // Arm 2 suspends normally and must deliver only its own payload.
        fake.Mode = FakeReadMode.Suspend;
        var second = reader.ReadAsync();
        Ensure(!second.IsCompleted, "arm 2 must be pending");
        fake.Publish(Result(0x88));
        var secondResult = second.GetAwaiter().GetResult();
        Ensure(secondResult.Buffer.ToArray()[0] == 0x88,
            "arm 2 must deliver its own payload, not a replayed arm 1 result");
        reader.AdvanceTo(secondResult.Buffer.End);
    }

    // ========================================================================================
    // 3. Ownership invariants
    // ========================================================================================

    [Test]
    public async Task AdvanceToShouldReleaseOwnershipForTheNextRead()
    {
        var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
        var reader = new ReadOwnershipPipeReader(fake);

        var read = reader.ReadAsync();
        fake.Publish(Result(0x99));
        var result = read.GetAwaiter().GetResult();
        reader.AdvanceTo(result.Buffer.End);

        // Each buffer is advanced once. Repeating AdvanceTo with an already released cursor
        // is outside the PipeReader buffer lifetime contract.
        var next = reader.ReadAsync();
        Ensure(!next.IsCompleted, "the next read must retain ownership until its own result is advanced");
        fake.Publish(Result(0xAA));
        var nextResult = next.GetAwaiter().GetResult();
        Ensure(nextResult.Buffer.ToArray()[0] == 0xAA, "the next read must receive its own payload after ownership release");
        reader.AdvanceTo(nextResult.Buffer.End);
        await Task.CompletedTask;
    }

    [Test]
    public async Task FaultOnSuspendedReadShouldReleaseOwnershipWithoutAdvanceTo()
    {
        var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
        var reader = new ReadOwnershipPipeReader(fake);

        var read = reader.ReadAsync();
        fake.PublishFault(new IOException("boom"));
        await CaptureAsync(async () => await WithTimeout(read, "faulted read"));

        // No AdvanceTo follows a fault, so the wrapper must have released ownership itself and the
        // next read must be accepted.
        var next = reader.ReadAsync();
        Ensure(!next.IsCompleted, "ownership must have been released by the fault path");
        fake.Publish(Result(0xBB));
        var nextResult = next.GetAwaiter().GetResult();
        reader.AdvanceTo(nextResult.Buffer.End);
        Ensure(nextResult.Buffer.ToArray()[0] == 0xBB, "a read after a fault must succeed");
    }

    [Test]
    public async Task CanceledResultShouldRetainOwnershipUntilAdvanceTo()
    {
        // A ReadResult with IsCanceled is a SUCCESSFUL completion, so ownership is retained and the
        // consumer still owes an AdvanceTo - this is what lets CompleteAsync wait for release.
        var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
        var reader = new ReadOwnershipPipeReader(fake);

        var read = reader.ReadAsync();
        fake.Publish(new ReadResult(Sequence(0xCC), isCanceled: true, isCompleted: false));
        var result = read.GetAwaiter().GetResult();
        Ensure(result.IsCanceled, "the canceled result must be delivered as canceled");

        var completion = reader.CompleteAsync().AsTask();
        var observed = await Task.WhenAny(completion, Task.Delay(Timeout)).ConfigureAwait(false);
        Ensure(!ReferenceEquals(observed, completion),
            "completion must stay blocked while a canceled read is owned");
        Ensure(fake.CompleteAsyncCount == 0,
            "the inner reader must not complete before the canceled result is released");

        reader.AdvanceTo(result.Buffer.End);
        await WithTimeout(new ValueTask(completion), "completion after canceled-result release");
        Ensure(fake.CompleteAsyncCount == 1,
            "the inner reader must complete exactly once after AdvanceTo");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void CancelPendingReadShouldForwardAndPreserveCanceledResultOwnership(bool pending)
    {
        var fake = new FakePipeReader { CompleteOnCancel = true };
        var reader = new ReadOwnershipPipeReader(fake);
        var read = pending ? reader.ReadAsync() : default;
        reader.CancelPendingRead();
        Ensure(fake.CancelPendingReadCount == 1, "CancelPendingRead must forward exactly once");
        if (!pending)
            read = reader.ReadAsync();
        Ensure(read.IsCompletedSuccessfully, "inner cancellation must complete the pending or next read");
        var result = read.GetAwaiter().GetResult();
        Ensure(result.IsCanceled, "the inner canceled ReadResult must be preserved");
        Ensure(Capture(() => _ = reader.ReadAsync()) is InvalidOperationException,
            "a canceled result must retain buffer ownership until AdvanceTo");
        Ensure(Capture(() => reader.TryRead(out _)) is InvalidOperationException,
            "TryRead must not bypass canceled-result ownership");
        reader.AdvanceTo(result.Buffer.End);
        var next = reader.ReadAsync();
        fake.Publish(Result(0xCF));
        var nextResult = next.GetAwaiter().GetResult();
        Ensure(!nextResult.IsCanceled, "forwarded cancellation must not poison a later fresh arm");
        reader.AdvanceTo(nextResult.Buffer.End);
        Ensure(fake.CancelPendingReadCount == 1, "observing and releasing must not forward extra cancellations");
    }

    [Test]
    public void RepeatedCancelPendingReadShouldForwardEveryRequest()
    {
        var fake = new FakePipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var read = reader.ReadAsync();
        reader.CancelPendingRead();
        reader.CancelPendingRead();
        Ensure(fake.CancelPendingReadCount == 2, "each explicit cancellation request must reach the inner reader");
        Ensure(!read.IsCompleted, "the wrapper must not invent a result before the inner publishes");
        fake.Publish(Result(0xCD));
        var result = read.GetAwaiter().GetResult();
        Ensure(!result.IsCanceled, "the inner reader remains authoritative for cancellation semantics");
        reader.AdvanceTo(result.Buffer.End);
    }

    [Test]
    public async Task ConcurrentReadShouldBeRejected()
    {
        var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
        var reader = new ReadOwnershipPipeReader(fake);

        var first = reader.ReadAsync();
        var failure = Capture(() => _ = reader.ReadAsync());
        Ensure(failure is InvalidOperationException,
            $"a concurrent read must be rejected with InvalidOperationException (got {failure?.GetType().Name ?? "none"})");

        fake.Publish(Result(0xDD));
        var result = first.GetAwaiter().GetResult();
        reader.AdvanceTo(result.Buffer.End);
        await Task.CompletedTask;
    }

    // ========================================================================================
    // 4. Barrier-driven races
    // ========================================================================================

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompleteAsyncBeforeSuspensionIsArmedShouldWaitForOwnership(bool fault)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowReturn = new ManualResetEventSlim();
        var fake = new FakePipeReader
        {
            BeforeReadReturns = () =>
            {
                entered.TrySetResult();
                Ensure(allowReturn.Wait(Timeout), "the test must release the inner ReadAsync call");
            },
            OnCancelPendingRead = () => canceled.TrySetResult(),
        };
        var reader = new ReadOwnershipPipeReader(fake);
        var invocation = Task.Run<ValueTask<ReadResult>>(() => reader.ReadAsync());
        ValueTask completion = default;
        try
        {
            await entered.Task.WaitAsync(Timeout).ConfigureAwait(false);
            completion = reader.CompleteAsync();
            await canceled.Task.WaitAsync(Timeout).ConfigureAwait(false);
            Ensure(!completion.IsCompleted && fake.CompleteAsyncCount == 0,
                "completion must wait for ownership even before the suspended source is armed");
        }
        finally
        {
            allowReturn.Set();
        }

        var pending = await invocation.WaitAsync(Timeout).ConfigureAwait(false);
        Ensure(!pending.IsCompleted, "the acquired read must still arm after completion was requested");
        if (fault)
        {
            var expected = new IOException("failure after shutdown requested");
            fake.PublishFault(expected);
            await WithTimeout(completion, "completion after the inner fault released ownership");
            Ensure(ReferenceEquals(Capture(() => pending.GetAwaiter().GetResult()), expected),
                "transport cleanup must not invalidate the unobserved source failure");
        }
        else
        {
            fake.Publish(Result(0xAF));
            var result = pending.GetAwaiter().GetResult();
            Ensure(!completion.IsCompleted && fake.CompleteAsyncCount == 0,
                "the returned buffer must retain ownership until AdvanceTo");
            reader.AdvanceTo(result.Buffer.End);
            await WithTimeout(completion, "completion after the buffer was advanced");
        }
        Ensure(fake.CompleteAsyncCount == 1 && fake.CancelPendingReadCount == 1,
            "shutdown must forward cancellation and complete the inner reader exactly once");
    }

    [Test]
    public async Task CompleteAsyncRacingPendingReadShouldCompleteInnerExactlyOnce()
    {
        // CompleteAsync and the inner publication are released from two different threads behind a
        // barrier, so the terminal transition and the deferred completion genuinely interleave.
        const int iterations = 2_000;
        for (var i = 0; i < iterations; i++)
        {
            var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
            var reader = new ReadOwnershipPipeReader(fake);

            var pending = reader.ReadAsync();
            using var barrier = new Barrier(2);

            var completionTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await reader.CompleteAsync().ConfigureAwait(false);
            });

            await Task.Run(() =>
            {
                barrier.SignalAndWait();
                fake.Publish(new ReadResult(Sequence(0xEE), isCanceled: true, isCompleted: false));
            });

            var result = pending.GetAwaiter().GetResult();
            reader.AdvanceTo(result.Buffer.End);
            await WithTimeout(new ValueTask(completionTask), "CompleteAsync after release");

            Ensure(fake.CompleteAsyncCount == 1,
                $"the inner reader must be completed exactly once (iteration {i}, count {fake.CompleteAsyncCount})");
            Ensure(fake.OutstandingArms == 0,
                $"no arm may stay outstanding after the race (iteration {i})");
        }
    }

    [Test]
    public async Task DisposalWhileSuspendedShouldNotCompleteInnerBeforeRelease()
    {
        for (var i = 0; i < 500; i++)
        {
            var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
            var reader = new ReadOwnershipPipeReader(fake);

            var pending = reader.ReadAsync();
            var completion = reader.CompleteAsync();

            fake.Publish(new ReadResult(Sequence(0xFF), isCanceled: true, isCompleted: false));
            var result = pending.GetAwaiter().GetResult();

            Ensure(fake.CompleteAsyncCount == 0,
                $"the inner reader must not be completed while the consumer still owns a ReadResult (iteration {i})");

            reader.AdvanceTo(result.Buffer.End);
            await WithTimeout(completion, "deferred completion");
            Ensure(fake.CompleteAsyncCount == 1,
                $"the inner reader must be completed exactly once after release (iteration {i})");
        }
    }

    [Test]
    public async Task ManyReadersUnderConcurrentLoadShouldNotCrossTalkOrLeak()
    {
        // Many independent readers use the shared BCL state-machine pool concurrently. Incorrect
        // state reuse or ownership release would surface as a wrong payload or a leaked arm.
        const int readers = 32;
        const int roundsPerReader = 2_000;

        var tasks = new Task[readers];
        for (var r = 0; r < readers; r++)
        {
            var payload = (byte)(r + 1);
            tasks[r] = Task.Run(async () =>
            {
                var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
                var reader = new ReadOwnershipPipeReader(fake);

                for (var n = 0; n < roundsPerReader; n++)
                {
                    var read = reader.ReadAsync();
                    Ensure(!read.IsCompleted, $"reader {payload} round {n}: read must be pending");
                    fake.Publish(Result(payload));
                    var result = read.GetAwaiter().GetResult();
                    var actual = result.Buffer.ToArray()[0];
                    Ensure(actual == payload,
                        $"reader {payload} round {n}: cross-talk, observed {actual}");
                    reader.AdvanceTo(result.Buffer.End);
                }

                Ensure(fake.OutstandingArms == 0, $"reader {payload}: arms leaked");
            });
        }

        await WithTimeout(new ValueTask(Task.WhenAll(tasks)), "concurrent readers");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void UnobservedFailureMustNotCorruptANewerRead(bool canceled)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception failure = canceled ? new OperationCanceledException(cancellation.Token) : new IOException("faulted");
        var fake = new FakePipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var failed = reader.ReadAsync();
        fake.PublishFault(failure);
        Ensure(canceled ? failed.IsCanceled : failed.IsFaulted, "the old operation must receive its own failure");

        // Fault publication releases transport ownership without waiting for observation. The
        // unconsumed pooled box belongs to the old operation, not to a per-reader fault lease.
        var next = reader.ReadAsync();
        Ensure(!next.IsCompleted, "the new operation must wait for its own publication");
        Ensure(ReferenceEquals(Capture(() => failed.GetAwaiter().GetResult()), failure),
            "observing the old failure must preserve its exception and cancellation token");
        Ensure(!next.IsCompleted, "observing the old failure must not complete the new operation");
        Ensure(Capture(() => reader.ReadAsync()) is InvalidOperationException,
            "observing the old failure must not release the new operation's ownership");
        Ensure(Capture(() => reader.TryRead(out _)) is InvalidOperationException,
            "TryRead must not bypass the new operation's ownership");
        fake.Publish(Result(0xA1));
        var result = next.GetAwaiter().GetResult();
        Ensure(result.Buffer.ToArray()[0] == 0xA1, "the new read must receive its own result");
        reader.AdvanceTo(result.Buffer.End);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnobservedFaultShouldNotDelayTransportCompletion(bool completeBeforeFailure)
    {
        var fake = new FakePipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var failure = new IOException("faulted");
        var read = reader.ReadAsync();
        var completion = completeBeforeFailure ? reader.CompleteAsync() : default;
        if (completeBeforeFailure)
            Ensure(!completion.IsCompleted, "completion must wait for the pending read's ownership");
        fake.PublishFault(failure);
        if (!completeBeforeFailure)
            completion = reader.CompleteAsync();
        await WithTimeout(completion, "completion with an unobserved fault");
        Ensure(fake.CompleteAsyncCount == 1, "completion must not require observing the old fault");
        Ensure(read.IsFaulted, "transport completion must preserve the failed operation");
        Ensure(ReferenceEquals(Capture(() => read.GetAwaiter().GetResult()), failure),
            "the original exception must remain available for its single observation");
    }

    [Test]
    public async Task UnobservedFailuresMustRemainIndependentAcrossNewerOperations()
    {
        var fake = new FakePipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var first = reader.ReadAsync();
        var firstFailure = new IOException("first fault");
        fake.PublishFault(firstFailure);
        var second = reader.ReadAsync();
        var secondFailure = new IOException("second fault");
        fake.PublishFault(secondFailure);
        var third = reader.ReadAsync();

        // Observe out of publication order, once per operation. No old fault lease or stale
        // token is required to protect either unobserved error or the currently pending read.
        Ensure(ReferenceEquals(Capture(() => second.GetAwaiter().GetResult()), secondFailure),
            "the second operation must preserve its own exception");
        Ensure(ReferenceEquals(Capture(() => first.GetAwaiter().GetResult()), firstFailure),
            "the first operation must preserve its own exception");
        Ensure(!third.IsCompleted, "observing older failures must not complete the newest operation");
        Ensure(Capture(() => reader.ReadAsync()) is InvalidOperationException,
            "observing older failures must not release the newest operation's ownership");
        fake.Publish(Result(0xA3));
        var result = third.GetAwaiter().GetResult();
        Ensure(result.Buffer.ToArray()[0] == 0xA3, "the newest operation must preserve its payload");
        reader.AdvanceTo(result.Buffer.End);
        await WithTimeout(reader.CompleteAsync(), "completion after independent operations");
    }

    [Test]
    public async Task FaultContinuationShouldBeAbleToObserveAndRearmInline()
    {
        var fake = new FakePipeReader();
        var reader = new ReadOwnershipPipeReader(fake);
        var failure = new IOException("faulted");
        var first = reader.ReadAsync();
        ValueTask<ReadResult> second = default;
        var ran = 0;
        first.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
        {
            Ensure(ReferenceEquals(Capture(() => first.GetAwaiter().GetResult()), failure),
                "the inline consumer must observe the original fault");
            second = reader.ReadAsync();
            ran++;
        });

        fake.PublishFault(failure);
        Ensure(ran == 1, "the failure continuation must run inline exactly once");
        Ensure(!second.IsCompleted, "the old callback must not complete the newly armed source");
        Ensure(Capture(() => _ = reader.ReadAsync()) is InvalidOperationException,
            "the fault continuation must leave the new read owning its buffer");
        fake.Publish(Result(0xA2));
        var result = await WithTimeout(second, "read rearmed by fault continuation");
        Ensure(result.Buffer.ToArray()[0] == 0xA2, "the rearmed read must receive its own payload");
        reader.AdvanceTo(result.Buffer.End);
    }

    // ========================================================================================
    // Helpers
    // ========================================================================================

    private static ReadOnlySequence<byte> Sequence(byte value) => new(new[] { value });

    private static ReadResult Result(byte value) => new(Sequence(value), isCanceled: false, isCompleted: false);

    private static async Task<T> WithTimeout<T>(ValueTask<T> task, string what)
    {
        var asTask = task.AsTask();
        var completed = await Task.WhenAny(asTask, Task.Delay(Timeout)).ConfigureAwait(false);
        Ensure(ReferenceEquals(completed, asTask),
            $"'{what}' did not complete within {Timeout.TotalSeconds:0}s - a lost continuation must fail rather than hang");
        return await asTask.ConfigureAwait(false);
    }

    private static async Task WithTimeout(ValueTask task, string what)
    {
        var asTask = task.AsTask();
        var completed = await Task.WhenAny(asTask, Task.Delay(Timeout)).ConfigureAwait(false);
        Ensure(ReferenceEquals(completed, asTask),
            $"'{what}' did not complete within {Timeout.TotalSeconds:0}s");
        await asTask.ConfigureAwait(false);
    }

    private static async Task WithTimeout(ValueTask<Task> task, string what)
        => await WithTimeout(new ValueTask(await task.ConfigureAwait(false)), what);

    private static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return null!;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    // ========================================================================================
    // Fake inner reader
    // ========================================================================================

    private enum FakeReadMode
    {
        /// <summary>The read pends until the test publishes.</summary>
        Suspend,

        /// <summary>The read reports Pending when inspected, then completes inline on subscription.</summary>
        CompleteOnSubscribe,
    }

    private sealed class FakePipeReader : PipeReader, IValueTaskSource<ReadResult>
    {
        private ManualResetValueTaskSourceCore<ReadResult> _source;
        private CompleteOnSubscribeSource? _inline;
        private bool _armed;
        private bool _cancelNextRead;

        public bool CompleteOnCancel { get; set; }

        public Action? BeforeReadReturns { get; set; }

        public Action? OnCancelPendingRead { get; set; }

        public FakeReadMode Mode { get; set; } = FakeReadMode.Suspend;

        public int AdvanceCount { get; private set; }

        public int CancelPendingReadCount { get; private set; }

        public int CompleteAsyncCount { get; private set; }

        public int OutstandingArms => _armed ? 1 : 0;

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (Mode == FakeReadMode.CompleteOnSubscribe)
            {
                _inline ??= new CompleteOnSubscribeSource();
                _inline.Next = Result(0x77);
                _inline.OnCompletedCallback = () => _armed = false;
                _armed = true;
                return new ValueTask<ReadResult>(_inline, _inline.Version);
            }

            _source.Reset();
            _armed = true;
            if (_cancelNextRead)
            {
                _cancelNextRead = false;
                Publish(new ReadResult(Sequence(0xCE), isCanceled: true, isCompleted: false));
            }
            BeforeReadReturns?.Invoke();
            return new ValueTask<ReadResult>(this, _source.Version);
        }

        public void Publish(ReadResult result)
        {
            _armed = false;
            _source.SetResult(result);
        }

        public void PublishFault(Exception exception)
        {
            _armed = false;
            _source.SetException(exception);
        }

        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => AdvanceCount++;

        public override void CancelPendingRead()
        {
            CancelPendingReadCount++;
            OnCancelPendingRead?.Invoke();
            if (!CompleteOnCancel)
                return;
            if (_armed)
                Publish(new ReadResult(Sequence(0xCE), isCanceled: true, isCompleted: false));
            else
                _cancelNextRead = true;
        }

        public override void Complete(Exception? exception = null)
        {
        }

        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            CompleteAsyncCount++;
            _armed = false;
            return default;
        }

        public override bool TryRead(out ReadResult result)
        {
            result = default;
            return false;
        }

        ReadResult IValueTaskSource<ReadResult>.GetResult(short token) => _source.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<ReadResult>.GetStatus(short token) => _source.GetStatus(token);

        void IValueTaskSource<ReadResult>.OnCompleted(
            Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _source.OnCompleted(continuation, state, token, flags);
    }

    /// <summary>
    /// A source that reports Pending when inspected but completes inline the moment a continuation
    /// is registered - reproducing the window between <c>ReadAsync</c> checking
    /// <c>IsCompletedSuccessfully</c> and registering its continuation.
    /// </summary>
    private sealed class CompleteOnSubscribeSource : IValueTaskSource<ReadResult>
    {
        private short _version = 1;
        private Action<object?>? _continuation;
        private object? _state;

        public ReadResult Next { get; set; }

        public Action? OnCompletedCallback { get; set; }

        public short Version => _version;

        ReadResult IValueTaskSource<ReadResult>.GetResult(short token)
        {
            if (token != _version)
                throw new InvalidOperationException("stale token");
            return Next;
        }

        ValueTaskSourceStatus IValueTaskSource<ReadResult>.GetStatus(short token)
            => _continuation is null ? ValueTaskSourceStatus.Pending : ValueTaskSourceStatus.Succeeded;

        void IValueTaskSource<ReadResult>.OnCompleted(
            Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _continuation = continuation;
            _state = state;
            // Complete inline, inside the subscription call.
            OnCompletedCallback?.Invoke();
            continuation(state);
        }
    }
}
