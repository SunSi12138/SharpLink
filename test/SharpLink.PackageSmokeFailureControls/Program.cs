using System.Runtime.CompilerServices;
using SharpLink.PackageSmoke;

internal static class Program
{
    private static async Task Main()
    {
        (string Name, Func<Task> Run)[] controls =
        [
            (nameof(SoleFailurePreservesIdentityAndStack), SoleFailurePreservesIdentityAndStack),
            (nameof(BodyPrecedesCompleteCleanupSiblingGraphs), BodyPrecedesCompleteCleanupSiblingGraphs),
            (nameof(SameExceptionInIndependentStagesIsNotDeduplicated), SameExceptionInIndependentStagesIsNotDeduplicated),
            (nameof(CancellationIsNotGenerallySuppressed), CancellationIsNotGenerallySuppressed),
            (nameof(AcceptedTaskIsExactAndMustAlreadyBeComplete), AcceptedTaskIsExactAndMustAlreadyBeComplete),
            (nameof(DiagnosticDeadlineDoesNotReleasePendingOperation), DiagnosticDeadlineDoesNotReleasePendingOperation),
            (nameof(SynchronousCleanupFailureDoesNotSkipLaterAttempts), SynchronousCleanupFailureDoesNotSkipLaterAttempts),
            (nameof(BothCleanupBranchesStartDespiteFirstSynchronousFailure), BothCleanupBranchesStartDespiteFirstSynchronousFailure),
            (nameof(CanceledAndFaultedCleanupBranchesBothSurvive), CanceledAndFaultedCleanupBranchesBothSurvive),
            (nameof(TwoCanceledCleanupStagesPreserveBothOccurrences), TwoCanceledCleanupStagesPreserveBothOccurrences)
        ];
        foreach (var control in controls)
        {
            try { await control.Run(); }
            catch
            {
                Console.WriteLine($"PACKAGE_SMOKE_FAILURE_CONTROL_FAIL name={control.Name}");
                throw;
            }
            Console.WriteLine($"PACKAGE_SMOKE_FAILURE_CONTROL_PASS name={control.Name}");
        }
        Console.WriteLine("PACKAGE_SMOKE_FAILURE_CONTROLS_PASS count=10");
    }

    private static Task SoleFailurePreservesIdentityAndStack()
    {
        var error = new InvalidOperationException("sole-body");
        var failures = new SmokeFailureCollector();
        failures.Attempt(() => ThrowOriginal(error));
        var observed = ThrownBy(failures.ThrowIfAny);
        Require(ReferenceEquals(observed, error), "sole exception identity changed");
        Require(observed.StackTrace?.Contains(nameof(ThrowOriginal), StringComparison.Ordinal) == true,
            "original throw stack was lost");
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOriginal(Exception error) => throw error;

    private static async Task BodyPrecedesCompleteCleanupSiblingGraphs()
    {
        var body = new InvalidOperationException("body");
        var first = new InvalidOperationException("cleanup-first");
        var nested = new AggregateException(new ArgumentException("nested-a"), new IOException("nested-b"));
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operation.SetException([first, nested]);
        var failures = new SmokeFailureCollector();
        failures.Capture(body);
        await failures.ObserveAsync(operation.Task);
        var actual = AggregateThrownBy(failures.ThrowIfAny);
        Require(actual.InnerExceptions.Count == 3, "a cleanup sibling was lost or flattened");
        Require(ReferenceEquals(actual.InnerExceptions[0], body), "body is not first");
        Require(ReferenceEquals(actual.InnerExceptions[1], first), "first cleanup identity changed");
        Require(ReferenceEquals(actual.InnerExceptions[2], nested) && nested.InnerExceptions.Count == 2,
            "nested cleanup graph changed");
        var bodyOperation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        bodyOperation.SetException([first, nested]);
        var bodyTask = SmokeFailureCollector.AwaitAsync(bodyOperation.Task);
        var bodyTaskFailures = new SmokeFailureCollector();
        await bodyTaskFailures.ObserveAsync(bodyTask);
        var bodyGraph = AggregateThrownBy(bodyTaskFailures.ThrowIfAny);
        Require(bodyGraph.InnerExceptions.Count == 2 && ReferenceEquals(bodyGraph.InnerExceptions[0], first) &&
                ReferenceEquals(bodyGraph.InnerExceptions[1], nested), "body task sibling graph was lost");
    }

    private static Task SameExceptionInIndependentStagesIsNotDeduplicated()
    {
        var shared = new InvalidOperationException("same-object-two-stages");
        var failures = new SmokeFailureCollector();
        failures.Capture(shared);
        failures.Attempt(() => throw shared);
        var actual = AggregateThrownBy(failures.ThrowIfAny);
        Require(actual.InnerExceptions.Count == 2 && actual.InnerExceptions.All(x => ReferenceEquals(x, shared)),
            "independent stage occurrences were deduplicated");
        return Task.CompletedTask;
    }

    private static async Task CancellationIsNotGenerallySuppressed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failures = new SmokeFailureCollector();
        await failures.ObserveAsync(Task.FromCanceled(cancellation.Token));
        Require(ThrownBy(failures.ThrowIfAny) is OperationCanceledException canceled &&
                canceled.CancellationToken == cancellation.Token, "canceled task was suppressed");
        var fault = new OperationCanceledException("faulted OCE is still a failure", cancellation.Token);
        var faulted = new SmokeFailureCollector();
        await faulted.ObserveAsync(Task.FromException(fault));
        Require(ReferenceEquals(ThrownBy(faulted.ThrowIfAny), fault), "faulted OCE was suppressed");
    }

    private static async Task AcceptedTaskIsExactAndMustAlreadyBeComplete()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var accepted = Task.FromCanceled(cancellation.Token);
        var failures = new SmokeFailureCollector();
        await failures.ObserveUnlessAcceptedAsync(accepted, accepted);
        failures.ThrowIfAny();
        var unrelated = new OperationCanceledException("different task", cancellation.Token);
        await failures.ObserveUnlessAcceptedAsync(Task.FromException(unrelated), accepted);
        Require(ReferenceEquals(ThrownBy(failures.ThrowIfAny), unrelated), "receipt excused another task");
        var sibling = new IOException("independent sibling beside an asserted cancellation");
        var multiple = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        multiple.SetException([unrelated, sibling]);
        var multipleFailures = new SmokeFailureCollector();
        await multipleFailures.ObserveUnlessAcceptedAsync(multiple.Task, multiple.Task);
        var multipleGraph = AggregateThrownBy(multipleFailures.ThrowIfAny);
        Require(multipleGraph.InnerExceptions.Count == 2 && ReferenceEquals(multipleGraph.InnerExceptions[1], sibling),
            "an accepted await result consumed an unobserved fault sibling");

        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingFailures = new SmokeFailureCollector();
        var join = pendingFailures.ObserveUnlessAcceptedAsync(pending.Task, pending.Task);
        Require(!join.IsCompleted, "incomplete receipt bypassed the actual join");
        var late = new IOException("late-failure");
        pending.SetException(late);
        await join;
        Require(ReferenceEquals(ThrownBy(pendingFailures.ThrowIfAny), late), "incomplete receipt hid late failure");
    }

    private static async Task DiagnosticDeadlineDoesNotReleasePendingOperation()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new IOException("late-a");
        var second = new InvalidOperationException("late-b");
        var released = false;
        var cleanup = CompleteAsync();
        Require(!cleanup.IsCompleted && !released, "diagnostic deadline released a pending owner");
        pending.SetException([first, second]);
        var failures = new SmokeFailureCollector();
        await failures.ObserveAsync(cleanup);
        var actual = AggregateThrownBy(failures.ThrowIfAny);
        Require(released && actual.InnerExceptions.Count == 3 && actual.InnerExceptions[0] is TimeoutException,
            "diagnostic failure or actual completion was lost");
        Require(ReferenceEquals(actual.InnerExceptions[1], first) && ReferenceEquals(actual.InnerExceptions[2], second),
            "post-deadline original siblings were lost");

        async Task CompleteAsync()
        {
            try { await SmokeFailureCollector.JoinWithDiagnosticAsync(pending.Task, Task.CompletedTask, "control"); }
            finally { released = true; }
        }
    }

    private static async Task SynchronousCleanupFailureDoesNotSkipLaterAttempts()
    {
        var first = new IOException("first-sync");
        var second = new InvalidOperationException("second-sync");
        var finalAttempted = false;
        var failures = new SmokeFailureCollector();
        failures.Attempt(() => throw first);
        await failures.AttemptAsync(() => throw second);
        failures.Attempt(() => finalAttempted = true);
        var actual = AggregateThrownBy(failures.ThrowIfAny);
        Require(finalAttempted && actual.InnerExceptions.Count == 2 &&
                ReferenceEquals(actual.InnerExceptions[0], first) && ReferenceEquals(actual.InnerExceptions[1], second),
            "synchronous failure skipped a later cleanup or changed its outcome");
    }

    private static async Task BothCleanupBranchesStartDespiteFirstSynchronousFailure()
    {
        var first = new IOException("branch-one-sync-before-return");
        var siblingA = new IOException("branch-two-a");
        var siblingB = new InvalidOperationException("branch-two-b");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = false;
        var firstFailures = new SmokeFailureCollector();
        var secondFailures = new SmokeFailureCollector();
        var firstCleanup = firstFailures.AttemptAsync(() => throw first);
        var secondCleanup = secondFailures.AttemptAsync(() =>
        {
            secondStarted = true;
            return new ValueTask(pending.Task);
        });
        var combined = Task.WhenAll(firstCleanup, secondCleanup);
        Require(secondStarted && !combined.IsCompleted, "first synchronous throw prevented second cleanup start");
        pending.SetException([siblingA, siblingB]);
        await combined;
        var all = new SmokeFailureCollector();
        all.Attempt(firstFailures.ThrowIfAny);
        all.Attempt(secondFailures.ThrowIfAny);
        var actual = AggregateThrownBy(all.ThrowIfAny);
        Require(actual.InnerExceptions.Count == 2 && ReferenceEquals(actual.InnerExceptions[0], first),
            "first branch failure was lost");
        Require(actual.InnerExceptions[1] is AggregateException siblings && siblings.InnerExceptions.Count == 2 &&
                ReferenceEquals(siblings.InnerExceptions[0], siblingA) && ReferenceEquals(siblings.InnerExceptions[1], siblingB),
            "second branch sibling graph was lost");
    }

    private static async Task CanceledAndFaultedCleanupBranchesBothSurvive()
    {
        var body = new InvalidOperationException("body-before-mixed-cleanup");
        var cancellation = new OperationCanceledException("first-cleanup-canceled");
        var faultA = new IOException("second-cleanup-a");
        var faultB = new InvalidOperationException("second-cleanup-b");
        var faultGraph = new AggregateException(faultA, faultB);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var completed = 0;
        // Same production shape: start both async cleanup branches before observing any.
        // ThrowIfAny's sole OCE really makes the first returned Task Canceled.
        var canceledBranch = CleanupBranchAsync(cancellation, gate.Task,
            () => started++, () => Interlocked.Increment(ref completed));
        var faultedBranch = CleanupBranchAsync(faultGraph, gate.Task,
            () => started++, () => Interlocked.Increment(ref completed));
        Require(started == 2 && !canceledBranch.IsCompleted && !faultedBranch.IsCompleted,
            "both original cleanup branches must start before the join");
        var failures = new SmokeFailureCollector();
        failures.Capture(body);
        var join = failures.ObserveAllAsync(canceledBranch, faultedBranch);
        gate.SetResult();
        await join;
        Require(canceledBranch.IsCanceled && faultedBranch.IsFaulted && completed == 2,
            "control did not produce the real mixed canceled/faulted task outcomes");
        var actual = AggregateThrownBy(failures.ThrowIfAny);
        Require(actual.InnerExceptions.Count == 3 && ReferenceEquals(actual.InnerExceptions[0], body) &&
                ReferenceEquals(actual.InnerExceptions[1], cancellation) && ReferenceEquals(actual.InnerExceptions[2], faultGraph),
            "mixed cleanup lost the body, cancellation identity, or complete fault graph");
        Require(faultGraph.InnerExceptions.Count == 2 && ReferenceEquals(faultGraph.InnerExceptions[0], faultA) &&
                ReferenceEquals(faultGraph.InnerExceptions[1], faultB), "fault sibling identities changed");
    }

    private static async Task TwoCanceledCleanupStagesPreserveBothOccurrences()
    {
        var shared = new OperationCanceledException("same-object-two-canceled-stages");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var completed = 0;
        var first = CleanupBranchAsync(shared, gate.Task,
            () => started++, () => Interlocked.Increment(ref completed));
        var second = CleanupBranchAsync(shared, gate.Task,
            () => started++, () => Interlocked.Increment(ref completed));
        Require(started == 2 && !first.IsCompleted && !second.IsCompleted,
            "both canceled-stage owners must start before the join");
        var failures = new SmokeFailureCollector();
        var join = failures.ObserveAllAsync(first, second);
        gate.SetResult();
        await join;
        Require(first.IsCanceled && second.IsCanceled && completed == 2,
            "control did not produce two independently canceled task stages");
        var actual = AggregateThrownBy(failures.ThrowIfAny);
        Require(actual.InnerExceptions.Count == 2 && actual.InnerExceptions.All(error => ReferenceEquals(error, shared)),
            "all-canceled cleanup lost an independent stage or deduplicated its exception");
    }

    private static async Task CleanupBranchAsync(Exception outcome, Task gate, Action started, Action completed)
    {
        started();
        await gate;
        var cleanup = new SmokeFailureCollector();
        cleanup.Capture(outcome);
        completed();
        cleanup.ThrowIfAny();
    }

    private static Exception ThrownBy(Action action)
    {
        try { action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected a preserved failure.");
    }

    private static AggregateException AggregateThrownBy(Action action)
        => ThrownBy(action) as AggregateException ?? throw new InvalidOperationException("Expected the full failure graph.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
