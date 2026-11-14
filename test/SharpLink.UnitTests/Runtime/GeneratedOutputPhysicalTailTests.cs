using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public sealed class GeneratedOutputPhysicalTailTests
{
    [Test]
    public async Task PhysicalWorkFaultMustStillPrecedeUniqueDisposalAndCtsRelease()
    {
        var tail = new GeneratedOutputPhysicalTail();
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposal = new HeldDisposal();
        var cancellation = new CountingCancellation();
        tail.Add(work.Task);
        tail.Seal(cancellation, disposal);
        var join = tail.JoinAsync().AsTask();
        Ensure(disposal.Calls == 0 && cancellation.DisposeCount == 0, "pending work must precede disposal");
        work.SetException(new InvalidOperationException("late MoveNext failure"));
        await disposal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(disposal.Calls == 1 && !join.IsCompleted && cancellation.DisposeCount == 0,
            "late work failure cannot skip or concurrently start a second disposal");
        disposal.Release.SetResult();
        await join.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(disposal.Calls == 1 && cancellation.DisposeCount == 1, "full ordered chain releases exactly once");
    }
    [Test]
    public async Task CompletedFirstWorkMustNotSealLaterPhysicalDisposal()
    {
        var tail = new GeneratedOutputPhysicalTail();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CountingCancellation();
        tail.Add(first.Task);
        first.SetException(new InvalidOperationException("late producer failure"));
        tail.Add(disposal.Task);
        tail.Seal(cancellation);
        var join = tail.JoinAsync();
        Ensure(!join.IsCompleted && cancellation.DisposeCount == 0, "all registered work, not just the first, owns the CTS");
        disposal.SetException(new InvalidOperationException("late disposal failure"));
        await join.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(cancellation.DisposeCount == 1, "late faults preserve the chosen terminal and release once after both exits");
    }

    [Test]
    public async Task DuplicateWorkAliasesMustNotDuplicateReleaseAuthority()
    {
        var tail = new GeneratedOutputPhysicalTail();
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CountingCancellation();
        tail.Add(work.Task);
        tail.Add(work.Task);
        tail.Seal(cancellation);
        var join = tail.JoinAsync();
        Ensure(!join.IsCompleted && cancellation.DisposeCount == 0, "copied task aliases are not copies of CTS ownership");
        work.SetResult();
        await join.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(cancellation.DisposeCount == 1, "one sealed owner releases once");
    }

    [Test]
    public async Task SealMustCloseAdmissionAndRejectASecondOwner()
    {
        var tail = new GeneratedOutputPhysicalTail();
        var cancellation = new CountingCancellation();
        using var rejected = new CountingCancellation();
        Throws(() => tail.JoinAsync());
        tail.Add(Task.CompletedTask);
        tail.Seal(cancellation);
        Throws(() => tail.Add(Task.CompletedTask));
        Throws(() => tail.Seal(rejected));
        await tail.JoinAsync();
        Ensure(cancellation.DisposeCount == 1 && rejected.DisposeCount == 0, "failed second ownership transfer cannot release another CTS");
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("expected closed physical lifetime");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class HeldDisposal : IAsyncDisposable
    {
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync()
        {
            Calls++;
            Entered.SetResult();
            await Release.Task;
        }
    }

    private sealed class CountingCancellation : CancellationTokenSource
    {
        public int DisposeCount;
        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref DisposeCount);
            base.Dispose(disposing);
        }
    }
}
