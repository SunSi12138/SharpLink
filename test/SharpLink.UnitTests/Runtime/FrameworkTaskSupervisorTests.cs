using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public sealed class FrameworkTaskSupervisorTests
{
    [Test]
    public async Task SealAndDrainShouldWaitForEveryAcceptedTask()
    {
        var supervisor = new FrameworkTaskSupervisor();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Track(first.Task, "first", TaskObservationMode.FrameworkOwned, static _ => false);
        supervisor.Track(second.Task, "second", TaskObservationMode.ExternallyObserved, static _ => false);

        supervisor.Seal();
        var drain = supervisor.DrainAsync();
        first.TrySetResult();
        await Task.Yield();

        Ensure(!drain.IsCompleted, "drain must retain ownership while any accepted task is active");
        var active = supervisor.CaptureSnapshot();
        Ensure(active.IsSealed && !active.IsDrained, "snapshot must distinguish sealed from drained");
        Ensure(active.ActiveTasks == 1 && active.ExternallyObservedTasks == 1,
            "snapshot must retain the remaining task observation mode");

        second.TrySetResult();
        await drain;
        var completed = supervisor.CaptureSnapshot();
        Ensure(completed.IsDrained && completed.ActiveTasks == 0, "drain must publish an empty terminal snapshot");
        Ensure(completed.TotalTracked == 2, "snapshot must preserve the total registration count");
    }

    [Test]
    public async Task DrainBeforeSealShouldFailFastWithoutChangingState()
    {
        var supervisor = new FrameworkTaskSupervisor();

        Exception? failure = null;
        try
        {
            await supervisor.DrainAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is InvalidOperationException { Message: var message } &&
               message.Contains("sealed", StringComparison.Ordinal),
            "drain before seal must report the lifecycle contract");
        var snapshot = supervisor.CaptureSnapshot();
        Ensure(!snapshot.IsSealed && !snapshot.IsDrained && snapshot.TotalTracked == 0,
            "a rejected drain must not mutate supervisor state");
    }

    [Test]
    public async Task FrameworkOwnedFailuresShouldBeAggregatedWithNestedSiblingsPreserved()
    {
        var observed = new ConcurrentQueue<(string Operation, Exception Failure)>();
        var supervisor = new FrameworkTaskSupervisor((operation, failure) => observed.Enqueue((operation, failure)));
        var expectedTransport = new IOException("transport closed");
        var unexpected = new InvalidOperationException("worker invariant failed");
        supervisor.Track(
            Task.WhenAll(Task.FromException(expectedTransport), Task.FromException(unexpected)),
            "request-loop",
            TaskObservationMode.FrameworkOwned,
            static exception => exception is IOException);

        supervisor.Seal();
        var failure = await CaptureFailureAsync(supervisor.DrainAsync());

        Ensure(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 2,
            "a pre-seal nested failure must preserve both siblings");
        Ensure(ContainsReference(failure!, expectedTransport) && ContainsReference(failure!, unexpected),
            "drain must retain the original exception instances");
        Ensure(observed.Count == 2 && observed.All(static item => item.Operation == "request-loop"),
            "framework-owned failures must be logged once with their operation");
    }

    [Test]
    public async Task ShutdownExpectedFailureShouldBeSuppressedOnlyAfterSeal()
    {
        var observed = new ConcurrentQueue<Exception>();
        var supervisor = new FrameworkTaskSupervisor((_, failure) => observed.Enqueue(failure));
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Track(
            worker.Task,
            "heartbeat",
            TaskObservationMode.FrameworkOwned,
            static exception => exception is OperationCanceledException);

        supervisor.Seal();
        worker.TrySetCanceled();
        await supervisor.DrainAsync();

        var snapshot = supervisor.CaptureSnapshot();
        Ensure(snapshot.SuppressedShutdownFailures == 1 && snapshot.RetainedFailures == 0,
            "post-seal expected cancellation must be counted but not reported");
        Ensure(observed.IsEmpty, "expected shutdown cancellation must not emit an error log");
    }

    [Test]
    public async Task ExternallyObservedFailureShouldBeWaitedWithoutDuplicateReporting()
    {
        var logCount = 0;
        var supervisor = new FrameworkTaskSupervisor((_, _) => Interlocked.Increment(ref logCount));
        var initialConnectFailure = new InvalidOperationException("caller owns this failure");
        supervisor.Track(
            Task.FromException(initialConnectFailure),
            "initial-connect",
            TaskObservationMode.ExternallyObserved,
            static _ => false);

        supervisor.Seal();
        await supervisor.DrainAsync();

        var snapshot = supervisor.CaptureSnapshot();
        Ensure(logCount == 0 && snapshot.RetainedFailures == 0,
            "external observation must prevent both duplicate stop failure and duplicate logging");
        Ensure(snapshot.TotalTracked == 1 && snapshot.IsDrained,
            "external tasks must still participate in task ownership and drain");
    }

    [Test]
    public void TrackAfterDrainCompletesShouldRejectAndRemainFaultObservable()
    {
        var supervisor = new FrameworkTaskSupervisor();
        supervisor.Seal();
        _ = supervisor.DrainAsync();
        var rejectedTask = Task.FromException(new InvalidOperationException("rejected task fault"));

        Exception? failure = null;
        try
        {
            supervisor.Track(
                rejectedTask,
                "late-cleanup",
                TaskObservationMode.FrameworkOwned,
                static _ => false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is InvalidOperationException { Message: var message } &&
               message.Contains("late-cleanup", StringComparison.Ordinal),
            "a post-drain registration must fail with its operation name");
        var snapshot = supervisor.CaptureSnapshot();
        Ensure(snapshot.RejectedRegistrations == 1 && snapshot.ActiveTasks == 0 && snapshot.TotalTracked == 0,
            "rejected registration must be diagnosed without entering the active set");
        Ensure(rejectedTask.Exception is not null, "the rejected fault must remain explicitly observable");
    }

    [Test]
    public async Task NestedTrackAfterDrainStartsShouldRemainOwnedAndDiagnosed()
    {
        var supervisor = new FrameworkTaskSupervisor();
        var parent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Track(
            parent.Task,
            "parent-worker",
            TaskObservationMode.FrameworkOwned,
            static _ => false);
        supervisor.Seal();
        var drain = supervisor.DrainAsync();

        supervisor.Track(
            child.Task,
            "nested-cleanup",
            TaskObservationMode.FrameworkOwned,
            static _ => false);
        parent.TrySetResult();
        await Task.Yield();

        var active = supervisor.CaptureSnapshot();
        Ensure(!drain.IsCompleted && active.ActiveTasks == 1 && active.LateRegistrations == 1,
            "a nested cleanup started by an active parent must extend drain and be diagnosed as late");
        child.TrySetResult();
        await drain;
        Ensure(supervisor.CaptureSnapshot().IsDrained,
            "drain must complete only after the late nested cleanup finishes");
    }

    [Test]
    public async Task TrackAfterSealBeforeDrainShouldBeAcceptedAndDiagnosed()
    {
        var supervisor = new FrameworkTaskSupervisor();
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Seal();

        supervisor.Track(
            worker.Task,
            "late-worker",
            TaskObservationMode.FrameworkOwned,
            static _ => false);
        var active = supervisor.CaptureSnapshot();
        Ensure(active.IsSealed && active.ActiveTasks == 1 && active.LateRegistrations == 1,
            "a registration in the Seal/Drain handoff must remain owned and be diagnosed as late");

        var drain = supervisor.DrainAsync();
        Ensure(!drain.IsCompleted, "drain must wait for a late accepted registration");
        worker.TrySetResult();
        await drain;

        var completed = supervisor.CaptureSnapshot();
        Ensure(completed.IsDrained && completed.TotalTracked == 1 && completed.LateRegistrations == 1,
            "the diagnosed late registration must converge to the drained terminal snapshot");
    }

    [Test]
    public async Task DuplicateRegistrationShouldFailWithoutCorruptingOwnership()
    {
        var supervisor = new FrameworkTaskSupervisor();
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Track(
            worker.Task,
            "worker",
            TaskObservationMode.FrameworkOwned,
            static _ => false);

        Exception? failure = null;
        try
        {
            supervisor.Track(
                worker.Task,
                "duplicate-worker",
                TaskObservationMode.ExternallyObserved,
                static _ => false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is InvalidOperationException { Message: var message } &&
               message.Contains("already registered", StringComparison.Ordinal),
            "the same task must not acquire two registrations in one supervisor");
        var active = supervisor.CaptureSnapshot();
        Ensure(active.TotalTracked == 1 && active.ActiveTasks == 1 && active.FrameworkOwnedTasks == 1,
            "a rejected duplicate must preserve the original registration and counters");

        supervisor.Seal();
        worker.TrySetResult();
        await supervisor.DrainAsync();
        Ensure(supervisor.CaptureSnapshot().IsDrained,
            "the original registration must still drain after a duplicate attempt");
    }

    [Test]
    public async Task SharedCompletedTaskShouldSupportConcurrentLogicalRegistrations()
    {
        const int registrationCount = 100;
        var supervisor = new FrameworkTaskSupervisor();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrations = new Task[registrationCount];
        for (var index = 0; index < registrations.Length; index++)
        {
            registrations[index] = Task.Run(async () =>
            {
                await start.Task;
                supervisor.Track(
                    Task.CompletedTask,
                    "synchronous-worker",
                    TaskObservationMode.FrameworkOwned,
                    static _ => false);
            });
        }

        start.TrySetResult();
        await Task.WhenAll(registrations);
        supervisor.Seal();
        await supervisor.DrainAsync();

        var snapshot = supervisor.CaptureSnapshot();
        Ensure(snapshot.TotalTracked == registrationCount && snapshot.ActiveTasks == 0 && snapshot.IsDrained,
            "a shared synchronously-completed Task must represent every logical registration without false duplication");
    }

    [Test]
    public async Task FrameworkFailureRetentionShouldRemainBoundedAndReportOverflow()
    {
        const int failureCount = 65;
        var supervisor = new FrameworkTaskSupervisor();
        for (var index = 0; index < failureCount; index++)
        {
            supervisor.Track(
                Task.FromException(new InvalidOperationException($"failure-{index}")),
                $"worker-{index}",
                TaskObservationMode.FrameworkOwned,
                static _ => false);
        }

        supervisor.Seal();
        var failure = await CaptureFailureAsync(supervisor.DrainAsync());
        var snapshot = supervisor.CaptureSnapshot();

        Ensure(snapshot.RetainedFailures == 64 && snapshot.DroppedFailures == 1,
            "failure retention must be capped while preserving an explicit overflow count");
        Ensure(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 65,
            "drain must expose every retained failure plus one bounded-overflow diagnostic");
        Ensure(ContainsException(failure!, static exception =>
                exception is InvalidOperationException { Message: var message } &&
                message.Contains("dropped 1 additional failures", StringComparison.Ordinal)),
            "the aggregate must explain failures omitted by the retention bound");
    }

    [Test]
    public async Task CaptureSnapshotShouldBoundOperationMetadata()
    {
        const int taskCount = 40;
        var supervisor = new FrameworkTaskSupervisor();
        var workers = new TaskCompletionSource[taskCount];
        for (var index = 0; index < workers.Length; index++)
        {
            workers[index] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            supervisor.Track(
                workers[index].Task,
                $"worker-{index}",
                index % 2 == 0 ? TaskObservationMode.FrameworkOwned : TaskObservationMode.ExternallyObserved,
                static _ => false);
        }

        var snapshot = supervisor.CaptureSnapshot();
        Ensure(snapshot.ActiveTasks == taskCount && snapshot.Operations.Count == 32,
            "snapshot metadata must be capped independently from active ownership");
        Ensure(snapshot.TruncatedOperations == 8, "snapshot must report the number of omitted operation entries");
        Ensure(snapshot.FrameworkOwnedTasks == 20 && snapshot.ExternallyObservedTasks == 20,
            "bounded metadata must not weaken full active-mode counters");

        supervisor.Seal();
        for (var index = 0; index < workers.Length; index++)
            workers[index].TrySetResult();
        await supervisor.DrainAsync();
    }

    [Test]
    public async Task TrackAndSealShouldDrainNormalOrDiagnosedLateRegistrationAcrossOneHundredRounds()
    {
        for (var round = 0; round < 100; round++)
        {
            var supervisor = new FrameworkTaskSupervisor();
            var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var start = new ManualResetEventSlim();
            var track = Task.Run(() =>
            {
                start.Wait();
                supervisor.Track(
                    worker.Task,
                    "racing-worker",
                    TaskObservationMode.FrameworkOwned,
                    static _ => false);
            });
            var seal = Task.Run(() =>
            {
                start.Wait();
                supervisor.Seal();
            });

            start.Set();
            await Task.WhenAll(track, seal);
            var drain = supervisor.DrainAsync();
            var beforeRelease = supervisor.CaptureSnapshot();
            Ensure(!drain.IsCompleted && beforeRelease.ActiveTasks == 1 &&
                   beforeRelease.RejectedRegistrations == 0,
                "both Track/Seal winners must retain the task until completion");
            Ensure(beforeRelease.LateRegistrations is 0 or 1,
                "a Seal-winning interleaving must be diagnosed as one late registration");

            worker.TrySetResult();
            await drain;
            Ensure(supervisor.CaptureSnapshot().IsDrained,
                "every Track/Seal interleaving must converge to the drained state");
        }
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static bool ContainsReference(Exception exception, Exception expected)
    {
        if (ReferenceEquals(exception, expected))
            return true;
        if (exception is AggregateException aggregate)
        {
            for (var index = 0; index < aggregate.InnerExceptions.Count; index++)
            {
                if (ContainsReference(aggregate.InnerExceptions[index], expected))
                    return true;
            }
        }
        return exception.InnerException is { } inner && ContainsReference(inner, expected);
    }

    private static bool ContainsException(Exception exception, Func<Exception, bool> predicate)
    {
        if (predicate(exception))
            return true;
        if (exception is AggregateException aggregate)
        {
            for (var index = 0; index < aggregate.InnerExceptions.Count; index++)
            {
                if (ContainsException(aggregate.InnerExceptions[index], predicate))
                    return true;
            }
        }
        return exception.InnerException is { } inner && ContainsException(inner, predicate);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
