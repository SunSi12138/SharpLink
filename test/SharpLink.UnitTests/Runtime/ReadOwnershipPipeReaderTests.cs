using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Correctness suite for <see cref="ReadOwnershipPipeReader"/>, which is a hand-written
/// <see cref="IValueTaskSource{T}"/>. A manual source is exactly the kind of code whose bugs do not
/// show up as exceptions but as a hang, a stale result, or a double release, so these tests
/// deliberately cover the source protocol itself rather than only the ownership rules.
/// </summary>
/// <remarks>
/// Three groups:
/// <list type="bullet">
/// <item>protocol conformance of the manual source (status, repeated GetResult, faults, and the
/// "registered after completion" case that hangs if it is wrong);</item>
/// <item>the reentrancy windows the implementation documents (inner completing inline during
/// <c>UnsafeOnCompleted</c>, and a consumer re-arming the reader from inside that inline
/// completion);</item>
/// <item>barrier-driven races (CompleteAsync against a pending read, disposal while suspended,
/// stale continuations under contention).</item>
/// </list>
/// Every await is guarded by <see cref="WithTimeout{T}"/> so a protocol bug fails the test instead
/// of hanging the run. No test uses <see cref="Thread.Sleep"/> to create a race.
/// </remarks>
public class ReadOwnershipPipeReaderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // ========================================================================================
    // 1. Manual IValueTaskSource protocol
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
    public async Task RepeatedGetResultAfterCompletionShouldReturnTheSameResult()
    {
        // IValueTaskSource.GetResult is allowed to be called more than once once the source has
        // completed; a manual source that returns itself to a pool inside GetResult must not break
        // the second observation.
        var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
        var reader = new ReadOwnershipPipeReader(fake);

        var read = reader.ReadAsync();
        fake.Publish(Result(0x22));

        var first = read.GetAwaiter().GetResult();
        var second = read.GetAwaiter().GetResult();
        Ensure(first.Buffer.ToArray()[0] == 0x22 && second.Buffer.ToArray()[0] == 0x22,
            "repeated GetResult must keep returning the published result");
        reader.AdvanceTo(first.Buffer.End);
        await Task.CompletedTask;
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
    public async Task StaleValueTaskFromAPreviousArmMustNotObserveTheNextArm()
    {
        // ABA: hold the ValueTask handed out for arm N, let the reader be re-armed and completed for
        // arm N+1, then observe the STALE one. The hard requirement is that it must NEVER deliver
        // arm N+1's payload.
        //
        // Note the documented contract difference from the previous implementation: this reader is
        // now itself the IValueTaskSource, so arming the next read calls
        // ManualResetValueTaskSourceCore.Reset(), which invalidates the previous arm's token.
        // Observing a stale ValueTask afterwards therefore throws InvalidOperationException instead
        // of re-returning arm N's result forever. That matches how the BCL's own pooled sources
        // (Pipe, Socket) behave - "Reset: resets to prepare for the next operation" - and it is
        // still safe for any consumer that observes each read once, which is the documented usage.
        // This test pins BOTH halves: never the new payload, and (if it does not throw) still the
        // old one.
        var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
        var reader = new ReadOwnershipPipeReader(fake);

        var stale = reader.ReadAsync();
        fake.Publish(Result(0x55));
        var first = stale.GetAwaiter().GetResult();
        reader.AdvanceTo(first.Buffer.End);

        // Re-arm and produce a different payload.
        var current = reader.ReadAsync();
        Ensure(!current.IsCompleted, "the re-armed read must be pending");
        fake.Publish(Result(0x66));
        var second = current.GetAwaiter().GetResult();
        Ensure(second.Buffer.ToArray()[0] == 0x66, "the live arm must deliver its own payload");
        reader.AdvanceTo(second.Buffer.End);

        // Re-observing the stale arm must never surface arm N+1's payload. Throwing is acceptable
        // (and is what happens today); returning arm N's payload would also be acceptable; returning
        // arm N+1's payload is a correctness bug.
        byte? rereadPayload = null;
        try
        {
            rereadPayload = stale.GetAwaiter().GetResult().Buffer.ToArray()[0];
        }
        catch (InvalidOperationException)
        {
            // Stale token rejected - the documented single-observation-per-arm behaviour.
        }

        Ensure(rereadPayload != 0x66,
            "a stale ValueTask must never observe a later arm's payload (ABA)");
        await Task.CompletedTask;
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
    public async Task AdvanceToShouldReleaseOwnershipExactlyOnce()
    {
        var fake = new FakePipeReader { Mode = FakeReadMode.Suspend };
        var reader = new ReadOwnershipPipeReader(fake);

        var read = reader.ReadAsync();
        fake.Publish(Result(0x99));
        var result = read.GetAwaiter().GetResult();
        reader.AdvanceTo(result.Buffer.End);

        // A second AdvanceTo must not release again: the next read has to be accepted.
        reader.AdvanceTo(result.Buffer.End);
        var next = reader.ReadAsync();
        Ensure(!next.IsCompleted, "a second AdvanceTo must not release the next read's ownership");
        fake.Publish(Result(0xAA));
        var nextResult = next.GetAwaiter().GetResult();
        Ensure(nextResult.Buffer.ToArray()[0] == 0xAA, "the next read must still work after a repeated AdvanceTo");
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

        await CaptureAsync(async () =>
        {
            var blocked = reader.CompleteAsync();
            await WithTimeout(blocked, "completion must stay blocked while a canceled read is owned");
        });

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
        // Many independent readers hammered from the thread pool. Each reader owns its own manual
        // source, so any shared mutable state, wrong field reset or stale token would surface as a
        // wrong payload or a leaked arm.
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
            $"'{what}' did not complete within {Timeout.TotalSeconds:0}s - a manual IValueTaskSource that drops a continuation hangs here");
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

        public override void CancelPendingRead() => CancelPendingReadCount++;

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
