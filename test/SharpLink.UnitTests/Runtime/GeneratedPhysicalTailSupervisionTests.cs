using System.Linq;
using System.IO.Pipelines;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

// Calls the actual existing Runtime tail boundary; the test owner only supplies adversarial
// permit/report/registration callbacks. It is not a substitute tail or supervisor implementation.
public sealed class GeneratedPhysicalTailSupervisionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task AcceptedLateOperationDisposeAndLifetimeFailuresPrecedeTailRelease()
    {
        var operationFailure = new OperationCanceledException("late operation");
        var disposeFailure = new OperationCanceledException("physical dispose");
        var pending = NewSignal();
        var enumerator = new TestEnumerator(disposeFailure);
        var owner = new TestOwner();
        var lifetime = new CancellationTokenSource();
        using var callback = lifetime.Token.Register(() => throw new InvalidOperationException("actual cancellation callback"));
        Exception? lifetimeFailure = null;
        try { lifetime.Cancel(); }
        catch (Exception error) { lifetimeFailure = error; }
        Ensure(lifetimeFailure is AggregateException, "actual linked-lifetime callback produced its original aggregate");
        var finish = Finish(enumerator, pending.Task, lifetime, owner, lifetimeFailure);
        await finish.WaitAsync(Timeout);
        Ensure(owner.Retains == 1 && owner.Releases == 0 && enumerator.DisposeCount == 0,
            "detached exact task is registered while operation remains physically held");
        owner.Supervisor.Seal();
        pending.SetException(operationFailure);
        await owner.Released.Task.WaitAsync(Timeout);
        await owner.Supervisor.DrainAsync().WaitAsync(Timeout);
        Ensure(owner.Recorded.Count == 3 && ReferenceEquals(owner.Recorded[0].Failure, operationFailure) &&
            ReferenceEquals(owner.Recorded[1].Failure, disposeFailure) && ReferenceEquals(owner.Recorded[2].Failure, lifetimeFailure) &&
            owner.RecordedAtRelease == 3 && enumerator.DisposeCount == 1,
            "all exact raw stages commit to permit before decrement; tail does not report them twice");
        EnsureDisposed(lifetime);
    }

    [Test]
    public async Task ReporterFailureBeforeAcceptanceRetainsOriginalAndStillReleasesActualTail()
    {
        var original = new OperationCanceledException("untransferred raw");
        var reporting = new InvalidOperationException("report rejected before storage");
        var releasing = new OperationCanceledException("release infrastructure");
        var pending = NewSignal();
        var owner = new TestOwner { ReportingFailure = reporting, ReleaseFailure = releasing };
        var enumerator = new TestEnumerator();
        var lifetime = new CancellationTokenSource();
        await Finish(enumerator, pending.Task, lifetime, owner).WaitAsync(Timeout);
        owner.Supervisor.Seal();
        pending.SetException(original);
        var failure = await Failure(owner.Supervisor.DrainAsync());
        Ensure(Contains(failure, original) && Contains(failure, reporting) && Contains(failure, releasing) &&
            owner.Recorded.Count == 0 && owner.Releases == 1 && enumerator.DisposeCount == 1,
            "reporting/release failures cannot erase rejected raw fault or skip physical disposal");
        EnsureDisposed(lifetime);
    }

    [Test]
    public async Task ReporterThrowAfterAcceptanceDoesNotRecopyPermitOwnedOutcome()
    {
        var original = new InvalidOperationException("permit owned");
        var reporting = new OperationCanceledException("after accepted notification");
        var owner = new TestOwner { ReportingFailure = reporting, AcceptBeforeReportingFailure = true };
        var pending = NewSignal();
        await Finish(new TestEnumerator(), pending.Task, new CancellationTokenSource(), owner).WaitAsync(Timeout);
        owner.Supervisor.Seal();
        pending.SetException(original);
        var supervised = await Failure(owner.Supervisor.DrainAsync());
        Ensure(owner.Recorded.Count == 1 && ReferenceEquals(owner.Recorded[0].Failure, original) &&
            ReferenceEquals(supervised, reporting) && !Contains(supervised, original),
            "accepted raw fault remains exclusively permit-owned while distinct notification residual remains strict");
    }

    [Test]
    public async Task TrackingFailureBeforeAcceptanceJoinsSameHeldTailAndOwnsItsResidual()
    {
        var tracking = new InvalidOperationException("tracking before acceptance");
        var operation = new OperationCanceledException("unaccepted tail residual");
        var reporting = new InvalidOperationException("reporting infrastructure");
        var owner = new TestOwner { TrackingFailure = tracking, ReportingFailure = reporting };
        var pending = NewSignal();
        var enumerator = new TestEnumerator();
        var finish = Finish(enumerator, pending.Task, new CancellationTokenSource(), owner);
        Ensure(!finish.IsCompleted && owner.TrackedTask is not null && owner.Releases == 0,
            "rejected tracking cannot detach or cancel-wait the actual physical task");
        pending.SetException(operation);
        var failure = PhysicalTaskFailureException.GetRaw(await Failure(finish));
        Ensure(Contains(failure, tracking) && Contains(failure, operation) && Contains(failure, reporting) &&
            owner.Releases == 1 && enumerator.DisposeCount == 1 && owner.Supervisor.CaptureSnapshot().TotalTracked == 0,
            "unaccepted origin joins and owns both tracking and exact tail infrastructure failure");
    }

    [Test]
    public async Task TrackingThrowAfterAcceptanceKeepsSupervisorAsOnlyTailOutcomeOwner()
    {
        var tracking = new OperationCanceledException("tracking after receipt");
        var operation = new InvalidOperationException("accepted permit raw");
        var reporting = new InvalidOperationException("tail reporting infrastructure");
        var owner = new TestOwner
        {
            TrackingFailure = tracking, AcceptBeforeTrackingFailure = true,
            ReportingFailure = reporting, AcceptBeforeReportingFailure = true
        };
        var pending = NewSignal();
        var finish = Finish(new TestEnumerator(), pending.Task, new CancellationTokenSource(), owner);
        Ensure(!finish.IsCompleted && owner.Supervisor.CaptureSnapshot().ActiveTasks == 1,
            "post-receipt setup error still joins actual tail before fallback");
        owner.Supervisor.Seal();
        pending.SetException(operation);
        var origin = PhysicalTaskFailureException.GetRaw(await Failure(finish));
        var supervised = await Failure(owner.Supervisor.DrainAsync());
        Ensure(ReferenceEquals(origin, tracking) && ReferenceEquals(supervised, reporting) &&
            owner.Recorded.Count == 1 && ReferenceEquals(owner.Recorded[0].Failure, operation) &&
            owner.Supervisor.CaptureSnapshot().ActiveTasks == 0,
            "receipt partitions permit raw, accepted task residual, and distinct origin setup failure without duplication");
    }

    [Test]
    public async Task CompletedLateFailureStillTransfersAtCountZeroWithoutDetachedRegistration()
    {
        var raw = new OperationCanceledException("completed just before finally");
        var dispose = new InvalidOperationException("completed disposal failure");
        var owner = new TestOwner();
        var lifetime = new CancellationTokenSource();
        await Finish(new TestEnumerator(dispose), Task.FromException(raw), lifetime, owner).WaitAsync(Timeout);
        Ensure(owner.Retains == 0 && owner.Releases == 0 && owner.TrackedTask is null && owner.Recorded.Count == 2 &&
            ReferenceEquals(owner.Recorded[0].Failure, raw) && ReferenceEquals(owner.Recorded[1].Failure, dispose),
            "count-zero completion cannot silently erase independent already-finished physical outcomes");
        EnsureDisposed(lifetime);
    }

    [Test]
    public async Task MissingOwnerOrRetainFailureCannotOrphanHeldOperation()
    {
        var raw = new OperationCanceledException("ownerless actual operation");
        var pending = NewSignal();
        var enumerator = new TestEnumerator();
        var finish = Finish(enumerator, pending.Task, new CancellationTokenSource(), null);
        Ensure(!finish.IsCompleted && enumerator.DisposeCount == 0, "owner-null path explicitly retains actual inline join");
        pending.SetException(raw);
        Ensure(ReferenceEquals(PhysicalTaskFailureException.GetRaw(await Failure(finish)), raw) && enumerator.DisposeCount == 1,
            "ownerless OCE is enveloped after real disposal, not detached or canceled away");

        var retain = new InvalidOperationException("retain setup failed");
        var owner = new TestOwner { RetainFailure = retain };
        var second = NewSignal();
        var secondFinish = Finish(new TestEnumerator(), second.Task, new CancellationTokenSource(), owner);
        Ensure(!secondFinish.IsCompleted && owner.TrackedTask is null, "failed retain keeps exact task inline");
        second.SetResult();
        Ensure(ReferenceEquals(PhysicalTaskFailureException.GetRaw(await Failure(secondFinish)), retain) && owner.Releases == 0,
            "failed acquisition does not invent a release or lose setup failure");
    }

    [Test]
    public async Task SameExceptionReferenceFromIndependentStagesIsNotDeduplicated()
    {
        var same = new OperationCanceledException("same object, distinct stages");
        var pending = NewSignal();
        var owner = new TestOwner { ReportingFailure = same, ReleaseFailure = same };
        await Finish(new TestEnumerator(), pending.Task, new CancellationTokenSource(), owner).WaitAsync(Timeout);
        owner.Supervisor.Seal();
        pending.SetException(same);
        var failure = await Failure(owner.Supervisor.DrainAsync());
        Ensure(CountOccurrences(failure, same) == 3,
            "operation, reporter and release occurrences preserve provenance even when all share the exact Exception object");
    }

    [Test]
    public async Task OrdinaryPumpFinallyTransfersCallbackAndDisposeFailuresWithoutTailRegistration()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "ordinary-physical-finally", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var callback = new OperationCanceledException("normal-finally-callback");
        var disposal = new OperationCanceledException("normal-finally-dispose");
        var stream = new EmptyFaultingStream(callback, disposal);
        var owner = new TestOwner();
        await session.PumpGeneratedOutboundStreamAsync(101, 0, stream, new UnusedCodec(),
            payloadNullable: false, CancellationToken.None, owner);
        Ensure(owner.Retains == 0 && owner.Releases == 0 && owner.TrackedTask is null && owner.Recorded.Count == 2 &&
            owner.Recorded.Any(item => ReferenceEquals(item.Failure, disposal)) &&
            owner.Recorded.Any(item => Contains(item.Failure, callback)),
            "normal existing state-machine finally preserves physical callbacks/Dispose without a new tail Task");
        await session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private sealed class EmptyFaultingStream(Exception callbackFailure, Exception disposalFailure)
        : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public int Current => throw new InvalidOperationException("empty stream has no item");
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken.Register(() => throw callbackFailure);
            return this;
        }
        public ValueTask<bool> MoveNextAsync() => new(false);
        public ValueTask DisposeAsync() => ValueTask.FromException(disposalFailure);
    }

    private sealed class UnusedCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer) => throw new InvalidOperationException("No item expected.");
        public int Deserialize(in ReadOnlySequence<byte> buffer) => throw new InvalidOperationException("No decode expected.");
    }

    private static Task Finish(TestEnumerator enumerator, Task pending, CancellationTokenSource lifetime,
        IGeneratedStreamPhysicalOwner? owner, Exception? lifetimeFailure = null)
    {
        var method = typeof(RpcSession).GetMethod("FinishGeneratedEnumeratorAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(int));
        return ((ValueTask)method.Invoke(null, [enumerator, pending, lifetime, owner, true, lifetimeFailure])!).AsTask();
    }

    private sealed class TestEnumerator(Exception? disposeFailure = null) : IAsyncEnumerator<int>
    {
        internal int DisposeCount;
        public int Current => 0;
        public ValueTask<bool> MoveNextAsync() => throw new InvalidOperationException("Existing operation supplied directly to production finalizer.");
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return disposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(disposeFailure);
        }
    }

    private sealed class TestOwner : IGeneratedStreamPhysicalOwner
    {
        internal readonly FrameworkTaskSupervisor Supervisor = new();
        internal readonly List<(GeneratedPhysicalFailureStage Stage, Exception Failure)> Recorded = [];
        internal readonly TaskCompletionSource Released = NewSignal();
        internal Exception? RetainFailure, ReportingFailure, ReleaseFailure, TrackingFailure;
        internal bool AcceptBeforeReportingFailure, AcceptBeforeTrackingFailure;
        internal int Retains, Releases, RecordedAtRelease;
        internal Task? TrackedTask;
        public void RetainPhysicalTail()
        {
            if (RetainFailure is not null) throw RetainFailure;
            Retains++;
        }
        public void ReportPhysicalFailure(GeneratedPhysicalFailureStage stage, Exception failure, ref bool accepted)
        {
            if (ReportingFailure is null || AcceptBeforeReportingFailure)
            {
                Recorded.Add((stage, failure));
                accepted = true;
            }
            if (ReportingFailure is not null) throw ReportingFailure;
        }
        public void ReleasePhysicalTail()
        {
            RecordedAtRelease = Recorded.Count;
            Releases++;
            Released.TrySetResult();
            if (ReleaseFailure is not null) throw ReleaseFailure;
        }
        public void TrackPhysicalTail(Task task, ref FrameworkTaskRegistration? registration)
        {
            TrackedTask = task;
            if (TrackingFailure is null || AcceptBeforeTrackingFailure)
                Supervisor.TrackStrictPhysical(task, "actual-generated-tail", ref registration);
            if (TrackingFailure is not null) throw TrackingFailure;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool Contains(Exception failure, Exception expected) => CountOccurrences(failure, expected) != 0;
    private static int CountOccurrences(Exception failure, Exception expected)
        => ReferenceEquals(failure, expected) ? 1 : failure is AggregateException aggregate
            ? aggregate.InnerExceptions.Sum(inner => CountOccurrences(inner, expected)) : 0;
    private static void EnsureDisposed(CancellationTokenSource lifetime)
    {
        try { _ = lifetime.Token; }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Actual lifetime CTS must be disposed before owner release.");
    }
    private static async Task<Exception> Failure(Task task)
    {
        try { await task.WaitAsync(Timeout); }
        catch (Exception error) when (error is not TimeoutException) { return error; }
        throw new InvalidOperationException("Expected an independently retained failure.");
    }
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
