using System.Linq;

namespace SharpLink.UnitTests.Runtime;

public sealed class MailboxBufferedCleanupOwnershipTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public Task HeldCreditCleanupSurvivesLastChildReleaseAndDuplicateAbandon()
        => CheckOverlappingCleanupAsync(throwCredit: false);

    [Test]
    public Task ThrowingHeldCreditIsRetainedBeforePhysicalCompletion()
        => CheckOverlappingCleanupAsync(throwCredit: true);

    private static async Task CheckOverlappingCleanupAsync(bool throwCredit)
    {
        using var creditEntered = new ManualResetEventSlim();
        using var releaseCredit = new ManualResetEventSlim();
        using var dispatchEntered = new ManualResetEventSlim();
        using var releaseDispatch = new ManualResetEventSlim();
        var releasedBytes = 0;
        var mailbox = new PreAdmissionStreamDispatcher(RpcSessionTestFixture.RuntimeContext.Buffers,
            static _ => true, bytes => Interlocked.Add(ref releasedBytes, bytes), static () => { },
            retainUntilLocalCompletion: true);
        mailbox.SetBytesConsumedCallback((_, _, _) =>
        {
            creditEntered.Set();
            Ensure(releaseCredit.Wait(Timeout), "held credit callback released by test");
            if (throwCredit) throw new InvalidOperationException("held-credit-failure");
        }, 61, 1);
        await mailbox.DispatchAsync(new ReadOnlySequence<byte>(new byte[] { 1 }));
        await mailbox.DispatchAsync(new ReadOnlySequence<byte>(new byte[] { 2 }));
        var child = new BlockingReplayChild(dispatchEntered, releaseDispatch);
        Ensure(mailbox.TryBeginAttach(child, out _), "actual typed attachment claimed");
        var attach = Task.Run(() => mailbox.FinishAttach(child));
        Ensure(dispatchEntered.Wait(Timeout), "first actual replay dispatch is held");
        var abandon = Task.Run(() => CaptureFailure(() => mailbox.Abandon(out _)));
        try
        {
            Ensure(creditEntered.Wait(Timeout), "second buffered item's actual credit cleanup is held");
            releaseDispatch.Set();
            await attach.WaitAsync(Timeout); // Replay's finalizer and last child dispatch have returned.
            mailbox.Abandon(out _); // Duplicate must join existing owner, not start a new one.
            var physical = mailbox.WaitForPhysicalChildCleanupAsync().AsTask();
            Ensure(!physical.IsCompleted && child.DisposeCount == 0 && child.DrainedCount == 0,
                "last child release and duplicate abandon cannot outrun original credit cleanup owner");
            releaseCredit.Set();
            var abandonFailure = await abandon.WaitAsync(Timeout);
            Exception? physicalFailure = null;
            try { await physical.WaitAsync(Timeout); }
            catch (Exception error) when (error is not TimeoutException) { physicalFailure = error; }
            Ensure(child.DisposeCount == 1 && child.DrainedCount == 1 &&
                !((IStreamDispatchState)mailbox).HasActiveDispatches && mailbox.RetainedBytesForTests == 0 && releasedBytes == 2,
                "all buffer, dispatch, credit and actual child cleanup owners complete once");
            Ensure(throwCredit
                ? Contains(abandonFailure, "held-credit-failure") && Contains(physicalFailure, "held-credit-failure")
                : abandonFailure is null && physicalFailure is null,
                "physical completion retains actual cleanup failure rather than publishing early success");
        }
        finally
        {
            releaseDispatch.Set();
            releaseCredit.Set();
        }
    }

    [Test]
    public async Task AttachmentFailureAndThrowingBufferedCleanupReleaseCompletionUseIndependently()
    {
        var returnedBytes = 0;
        var creditCalls = 0;
        var mailbox = new PreAdmissionStreamDispatcher(RpcSessionTestFixture.RuntimeContext.Buffers,
            static _ => true, bytes =>
            {
                returnedBytes += bytes;
                throw new InvalidOperationException("buffer-budget-cleanup-failure");
            }, static () => { });
        mailbox.SetBytesConsumedCallback((_, _, _) =>
        {
            creditCalls++;
            throw new InvalidOperationException("credit-cleanup-failure");
        }, 62, 1);
        await mailbox.DispatchAsync(new ReadOnlySequence<byte>(new byte[] { 1 }));
        var child = new FailingAttachmentChild();
        Ensure(mailbox.TryBeginAttach(child, out _), "actual attachment starts before injected binding failure");
        var failure = CaptureFailure(() => mailbox.FinishAttach(child));
        Ensure(Contains(failure, "attachment-failure") && Contains(failure, "buffer-budget-cleanup-failure") &&
            Contains(failure, "credit-cleanup-failure") && Contains(failure, "completion-failure"),
            "original attachment and every independent cleanup failure remain observable");
        Ensure(child.CompleteCount == 1 && child.DrainedCount == 1 && returnedBytes == 1 && creditCalls == 1 &&
            mailbox.RetainedBytesForTests == 0 && !((IStreamDispatchState)mailbox).HasActiveDispatches,
            "throwing discard cannot strand the counted completion dispatch or finalization");
        mailbox.Abandon(out _);
        Exception? physicalFailure = null;
        try { await mailbox.WaitForPhysicalChildCleanupAsync().AsTask().WaitAsync(Timeout); }
        catch (Exception error) when (error is not TimeoutException) { physicalFailure = error; }
        Ensure(Contains(physicalFailure, "attachment-failure") && Contains(physicalFailure, "credit-cleanup-failure"),
            "later physical waiter sees failures recorded before the final dispatch use was released");
    }

    [Test]
    public async Task ReplayDispatchAndBufferedReleaseFailuresBothReachPhysicalCompletion()
    {
        var dispatchFailure = new InvalidOperationException("original-replay-dispatch-failure");
        var releaseFailure = new InvalidOperationException("original-replay-release-failure");
        var releaseCalls = 0;
        var mailbox = new PreAdmissionStreamDispatcher(RpcSessionTestFixture.RuntimeContext.Buffers,
            static _ => true, _ => { releaseCalls++; throw releaseFailure; }, static () => { });
        await mailbox.DispatchAsync(new ReadOnlySequence<byte>(new byte[] { 3 }));
        var child = new FailingReplayChild(dispatchFailure);
        Ensure(mailbox.TryBeginAttach(child, out _), "actual replay child attaches to original buffered item");
        var failure = CaptureFailure(() => mailbox.FinishAttach(child));
        Ensure(ContainsInstance(failure, dispatchFailure) && ContainsInstance(failure, releaseFailure),
            "buffer release must not replace the exact primary replay-dispatch exception");
        Ensure(releaseCalls == 1 && child.DispatchCount == 1 && child.CompleteCount == 1 &&
            child.DrainedCount == 1 && mailbox.RetainedBytesForTests == 0 &&
            !((IStreamDispatchState)mailbox).HasActiveDispatches,
            "both failures preserve exactly one buffer return and independently drained dispatch ownership");
        mailbox.Abandon(out _);
        Exception? physicalFailure = null;
        try { await mailbox.WaitForPhysicalChildCleanupAsync().AsTask().WaitAsync(Timeout); }
        catch (Exception error) when (error is not TimeoutException) { physicalFailure = error; }
        Ensure(ContainsInstance(physicalFailure, dispatchFailure) && ContainsInstance(physicalFailure, releaseFailure),
            "service-facing physical completion retains both exact original failure instances");
    }

    private sealed class FailingReplayChild(Exception failure) : IStreamDispatcher, IStreamDispatchLease
    {
        internal int DispatchCount;
        internal int CompleteCount;
        internal int DrainedCount;
        public void BindDispatchState(IStreamDispatchState state) { }
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount) => DispatchAsync(payload);
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) { DispatchCount++; return ValueTask.FromException(failure); }
        public void Complete(bool isError, string? errorMessage) => Complete(null);
        public void Complete(Exception? exception) => CompleteCount++;
        public void OnDispatchesDrained() => DrainedCount++;
    }

    private static bool ContainsInstance(Exception? failure, Exception expected)
        => ReferenceEquals(failure, expected) || failure is AggregateException aggregate &&
            aggregate.InnerExceptions.Any(inner => ContainsInstance(inner, expected));

    private sealed class BlockingReplayChild(ManualResetEventSlim entered, ManualResetEventSlim release)
        : IStreamDispatcher, IStreamDispatchLease, IAsyncDisposable
    {
        internal int DisposeCount;
        internal int DrainedCount;
        public void BindDispatchState(IStreamDispatchState state) { }
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount) => DispatchAsync(payload);
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            entered.Set();
            Ensure(release.Wait(Timeout), "actual replay dispatch released by test");
            return ValueTask.CompletedTask;
        }
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
        public void OnDispatchesDrained() => DrainedCount++;
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class FailingAttachmentChild : IStreamDispatcher, IStreamDispatchLease
    {
        internal int CompleteCount;
        internal int DrainedCount;
        public void BindDispatchState(IStreamDispatchState state) => throw new InvalidOperationException("attachment-failure");
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount) => ValueTask.CompletedTask;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) => Complete(null);
        public void Complete(Exception? exception) { CompleteCount++; throw new InvalidOperationException("completion-failure"); }
        public void OnDispatchesDrained() => DrainedCount++;
    }

    private static Exception? CaptureFailure(Action action)
    {
        try { action(); return null; }
        catch (Exception failure) { return failure; }
    }

    private static bool Contains(Exception? failure, string message)
        => failure?.Message == message || failure is AggregateException aggregate &&
            aggregate.InnerExceptions.Any(inner => Contains(inner, message));

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
