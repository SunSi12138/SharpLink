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

    // D/E/G/I/J/K are bounded Runtime component controls. The adversarial owner below
    // does not validate a real Server generation and is not a natural attachment-failure probe.
    [Test]
    public Task AcceptedInvocationKeepsActualRuntimeCancellationAndDisposeFailuresStrict()
        => ComponentFixture.RunAsync(holdDispose: false, async fixture =>
        {
            var sourceFailure = new OperationCanceledException("accepted source outcome");
            var same = new OperationCanceledException("independent callback and Dispose occurrences");
            fixture.Enumerator.CallbackFailure = same;
            fixture.Enumerator.DisposeFailure = same;
            fixture.Owner.InvocationHook = fixture.PublishValidReceipt;
            fixture.CreateSession();
            var session = fixture.Session!;
            using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(session.Id, authentication: null,
                       RpcDeadline.Create(TimeSpan.Zero, TimeProvider.System), TimeProvider.System)))
            {
                fixture.SetFinish(session.PumpGeneratedOutboundStreamAsync(ComponentFixture.RequestId, 0,
                    fixture.Enumerator, new UnusedCodec(), payloadNullable: false,
                    CancellationToken.None, fixture.Owner).AsTask());
            }
            var logical = await fixture.FailureAsync(fixture.FinishTask!);
            Ensure(logical is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "the actual Runtime deadline selects logical completion while the original MoveNext remains held");
            Ensure(fixture.Owner.InvocationAttempts == 1 && fixture.Owner.Retains == 1 &&
                fixture.Owner.Releases == 0 && fixture.Enumerator.DisposeCount == 0,
                "accepted pending source still retains the physical owner and is not disposed concurrently");
            fixture.Source.TrySetException(sourceFailure);
            await fixture.SuccessAsync(fixture.Drain());
            var recorded = fixture.ReadRecorded();
            Ensure(recorded.Length == 2 &&
                recorded[0].Stage == GeneratedPhysicalFailureStage.EnumeratorDispose &&
                ReferenceEquals(recorded[0].Failure, same) &&
                recorded[1].Stage == GeneratedPhysicalFailureStage.LifetimeCancellationDispose &&
                CountOccurrences(recorded[1].Failure, same) == 1 &&
                !recorded.Any(item => Contains(item.Failure, sourceFailure)) &&
                fixture.Owner.RecordedAtRelease == 2 && fixture.Owner.Releases == 1,
                "Runtime's own finally.Cancel and Dispose preserve two independent physical occurrences, not the accepted source");
            fixture.Enumerator.RequireCapturedLifetimeDisposed();
        });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task InvocationHookThrowPreservesOnlyItsAcceptedOutcomePartition(bool publishBeforeThrow)
        => ComponentFixture.RunAsync(holdDispose: false, async fixture =>
        {
            var source = new OperationCanceledException("original source");
            var hook = new OperationCanceledException("receipt hook infrastructure");
            fixture.Owner.InvocationHook = (Task operation, long requestId, ref GeneratedInvocationOutcomeReceipt? receipt) =>
            {
                if (publishBeforeThrow) fixture.PublishValidReceipt(operation, requestId, ref receipt);
                throw hook;
            };
            fixture.Start();
            await fixture.SuccessAsync(fixture.FinishTask!);
            Ensure(!fixture.Source.Task.IsCompleted && fixture.Enumerator.DisposeCount == 0,
                "ref receipt error does not discharge the real pending operation");
            fixture.Source.TrySetException(source);
            RequireExactRaw(await fixture.FailureAsync(fixture.Drain()), hook,
                "distinct hook failure remains the exact strict tail infrastructure outcome");
            var recorded = fixture.ReadRecorded();
            Ensure(publishBeforeThrow ? recorded.Length == 0 : recorded.Length == 1 &&
                recorded[0].Stage == GeneratedPhysicalFailureStage.PendingOperation && ReferenceEquals(recorded[0].Failure, source),
                "valid ref receipt survives throw, while pre-acceptance throw leaves original source strictly owned");
            Ensure(fixture.Owner.Releases == 1 && fixture.Enumerator.DisposeCount == 1,
                "actual source exit precedes once-only Dispose and release");
        });

    [Test]
    public Task IndependentPendingSendRoleCannotUseInvocationReceipt()
        => ComponentFixture.RunAsync(holdDispose: false, async fixture =>
        {
            var raw = new OperationCanceledException("independent pending send outcome");
            fixture.Owner.InvocationHook = fixture.PublishValidReceipt;
            fixture.Start(GeneratedPendingOperationRole.IndependentPhysical);
            await fixture.SuccessAsync(fixture.FinishTask!);
            Ensure(fixture.Owner.InvocationAttempts == 0, "independent physical role does not call the invocation receiver");
            fixture.Source.TrySetException(raw);
            await fixture.SuccessAsync(fixture.Drain());
            var recorded = fixture.ReadRecorded();
            Ensure(recorded.Length == 1 && recorded[0].Stage == GeneratedPhysicalFailureStage.PendingOperation &&
                ReferenceEquals(recorded[0].Failure, raw) && fixture.Owner.Releases == 1,
                "a terminal-capable owner cannot consume a send or infrastructure stage as business outcome");
        });

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public Task MalformedReceiptSurvivingHookThrowCannotConsumeOriginalSource(int malformedField)
        => ComponentFixture.RunAsync(holdDispose: false, async fixture =>
        {
            var source = new OperationCanceledException("malformed receipt source");
            var hook = new InvalidOperationException("malformed receipt hook failure");
            fixture.Owner.InvocationHook = (Task operation, long requestId, ref GeneratedInvocationOutcomeReceipt? receipt) =>
            {
                receipt = new GeneratedInvocationOutcomeReceipt(
                    malformedField == 0 ? new TestOwner() : fixture.Owner,
                    malformedField == 1 ? Task.CompletedTask : operation,
                    malformedField == 2 ? requestId + 1 : requestId, 1);
                throw hook;
            };
            fixture.Start();
            await fixture.SuccessAsync(fixture.FinishTask!);
            fixture.Source.TrySetException(source);
            var failure = await fixture.FailureAsync(fixture.Drain());
            var leaves = RawLeaves(failure).ToArray();
            Ensure(leaves.Length == 2 && leaves.Count(error => ReferenceEquals(error, hook)) == 1 &&
                leaves.Count(error => error is InvalidOperationException && !ReferenceEquals(error, hook) &&
                    error.Message == "The generated invocation outcome receipt does not match its exact owner, operation and request.") == 1,
                "post-catch validation retains independent malformed-receipt and hook failures without accepting the source");
            var recorded = fixture.ReadRecorded();
            Ensure(recorded.Length == 1 && recorded[0].Stage == GeneratedPhysicalFailureStage.PendingOperation &&
                ReferenceEquals(recorded[0].Failure, source) && fixture.Owner.Releases == 1,
                "wrong owner, Task or request never discharges original strict source ownership");
        });

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public Task AcceptedPendingSourceCompletingInsideReceiptHookUsesInlineFinalizer(int outcome)
        => ComponentFixture.RunAsync(holdDispose: false, async fixture =>
        {
            fixture.Owner.InvocationHook = (Task operation, long requestId, ref GeneratedInvocationOutcomeReceipt? receipt) =>
            {
                Ensure(ReferenceEquals(operation, fixture.Source.Task) && !operation.IsCompleted,
                    "actual finalizer receives the original nonnull still-pending operation");
                fixture.PublishValidReceipt(operation, requestId, ref receipt);
                switch (outcome)
                {
                    case 0: fixture.Source.TrySetResult(false); break;
                    case 1: fixture.Source.TrySetException(new InvalidOperationException("accepted inline ordinary fault")); break;
                    case 2: fixture.Source.TrySetException(new OperationCanceledException("accepted inline raw OCE")); break;
                    case 3: fixture.Source.TrySetCanceled(new CancellationToken(canceled: true)); break;
                    default: throw new ArgumentOutOfRangeException(nameof(outcome));
                }
            };
            fixture.Start();
            await fixture.SuccessAsync(fixture.FinishTask!);
            await fixture.SuccessAsync(fixture.Drain());
            Ensure(fixture.Owner.InvocationAttempts == 1 && fixture.Source.Task.IsCompleted &&
                fixture.Owner.Retains == 0 && fixture.Owner.Releases == 0 && fixture.Owner.TrackedTask is null &&
                fixture.ReadRecorded().Length == 0 && fixture.Enumerator.DisposeCount == 1,
                "receipt acceptance precedes completed-source branch, without source-ready pending clearing or detached registration");
            EnsureDisposed(fixture.Lifetime);
        });

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public Task AcceptedInvocationRetainOrTrackFailureJoinsSameSourceAndDispose(int setupFailurePoint)
        => ComponentFixture.RunAsync(holdDispose: true, async fixture =>
        {
            var source = new OperationCanceledException("accepted held source");
            var setup = new OperationCanceledException("independent retain or tracking failure");
            fixture.Owner.InvocationHook = fixture.PublishValidReceipt;
            if (setupFailurePoint == 0) fixture.Owner.RetainFailure = setup;
            else
            {
                fixture.Owner.TrackingFailure = setup;
                fixture.Owner.AcceptBeforeTrackingFailure = setupFailurePoint == 2;
            }
            fixture.Start();
            var finish = fixture.FinishTask!;
            Ensure(!finish.IsCompleted && !fixture.Source.Task.IsCompleted &&
                fixture.Enumerator.DisposeCount == 0,
                "failed retain/track cannot return or Dispose while accepted original source remains held");
            fixture.Source.TrySetException(source);
            await fixture.SuccessAsync(fixture.DisposeEntered.Task);
            Ensure(!finish.IsCompleted && fixture.Owner.Releases == 0,
                "same fallback still joins actual independent enumerator Dispose after source exit");
            fixture.DisposeRelease.TrySetResult();
            RequireExactRaw(await fixture.FailureAsync(finish), setup,
                "only the independent setup failure escapes, not the accepted original source outcome");
            await fixture.SuccessAsync(fixture.Drain());
            Ensure(fixture.ReadRecorded().Length == 0 && fixture.Enumerator.DisposeCount == 1 &&
                fixture.Owner.Releases == (setupFailurePoint == 0 ? 0 : 1) &&
                fixture.Owner.Supervisor.CaptureSnapshot().ActiveTasks == 0 &&
                fixture.Owner.Supervisor.CaptureSnapshot().TotalTracked == (setupFailurePoint == 2 ? 1 : 0) &&
                (setupFailurePoint != 2 || fixture.Owner.AcceptedTrackingReceipt is { IsStrictPhysical: true } receipt &&
                    ReferenceEquals(receipt.Task, fixture.Owner.TrackedTask)),
                "actual physical cleanup finishes once; accepted strict registration is completed without recopying its outcome");
            EnsureDisposed(fixture.Lifetime);
        });

    private delegate void InvocationReceiptHook(Task operation, long requestId,
        ref GeneratedInvocationOutcomeReceipt? receipt);

    private static IEnumerable<Exception> RawLeaves(Exception failure)
    {
        failure = PhysicalTaskFailureException.GetRaw(failure);
        if (failure is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                foreach (var leaf in RawLeaves(inner)) yield return leaf;
        }
        else yield return failure;
    }

    private static void RequireExactRaw(Exception actual, Exception expected, string message)
    {
        var raw = PhysicalTaskFailureException.GetRaw(actual);
        if (!ReferenceEquals(raw, expected))
            throw new AggregateException(new InvalidOperationException(message), raw);
    }

    // New controls retain every exact task and distinguish a bounded waiter timeout from a
    // source exception via successful Exception?-data observers. H9 above remains unchanged.
    private sealed class ComponentFixture
    {
        internal const long RequestId = 211;
        private static readonly System.Collections.Concurrent.ConcurrentBag<ComponentFixture> Quarantine = new();
        private readonly Dictionary<Task, TaskObservation> _observations = new();
        private int _recordedObserved;
        private Task? _drain, _sessionDispose, _inputComplete, _outputComplete;
        private Pipe? _input, _output;
        internal readonly TaskCompletionSource<bool> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeEntered = NewSignal();
        internal readonly TaskCompletionSource DisposeRelease = NewSignal();
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly TestOwner Owner = new();
        internal readonly ComponentEnumerator Enumerator;
        internal Task? FinishTask { get; private set; }
        internal RpcSession? Session { get; private set; }

        private ComponentFixture(bool holdDispose)
            => Enumerator = new ComponentEnumerator(Source.Task, DisposeEntered, DisposeRelease.Task, holdDispose);

        internal static async Task RunAsync(bool holdDispose, Func<ComponentFixture, Task> body)
        {
            var fixture = new ComponentFixture(holdDispose);
            var failures = new List<Exception>();
            try { await body(fixture).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            await fixture.CleanupAsync(failures).ConfigureAwait(false);
            if (failures.Count != 0)
                throw new AggregateException("Runtime component body or independent cleanup failed.", failures);
        }

        internal void PublishValidReceipt(Task operation, long requestId, ref GeneratedInvocationOutcomeReceipt? receipt)
            => receipt = new GeneratedInvocationOutcomeReceipt(Owner, operation, requestId, 1);

        internal void Start(GeneratedPendingOperationRole role = GeneratedPendingOperationRole.InvocationMoveNext)
            => SetFinish(Finish(Enumerator, Source.Task, Lifetime, Owner, null, role, RequestId));

        internal void SetFinish(Task task)
        {
            if (FinishTask is not null) throw new InvalidOperationException("Component invocation is owned once.");
            FinishTask = task;
        }

        internal void CreateSession()
        {
            _input = new Pipe();
            _output = new Pipe();
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                "accepted-runtime-cancel-finally", _input.Reader, _output.Writer, RpcSessionTestFixture.ServerOptions());
        }

        internal Task Drain()
        {
            Owner.Supervisor.Seal();
            return _drain ??= Owner.Supervisor.DrainAsync();
        }

        internal (GeneratedPhysicalFailureStage Stage, Exception Failure)[] ReadRecorded()
        {
            var rows = Owner.Recorded.ToArray();
            _recordedObserved = rows.Length;
            return rows;
        }

        internal async Task SuccessAsync(Task task)
        {
            var error = await TakeOutcomeAsync(Observe(task, reportUnobserved: true)).ConfigureAwait(false);
            if (error is not null) throw new AggregateException("Expected actual task success.", error);
        }

        internal async Task<Exception> FailureAsync(Task task)
            => await TakeOutcomeAsync(Observe(task, reportUnobserved: true)).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Expected an actual retained failure.");

        private TaskObservation Observe(Task task, bool reportUnobserved)
        {
            if (!_observations.TryGetValue(task, out var observation))
            {
                observation = new TaskObservation(task, reportUnobserved);
                _observations.Add(task, observation);
            }
            return observation;
        }

        private static async Task<Exception?> TakeOutcomeAsync(TaskObservation observation)
        {
            var error = await observation.Outcome.WaitAsync(Timeout).ConfigureAwait(false);
            observation.Consumed = true;
            return error;
        }

        private async Task JoinUnobservedAsync(List<Exception> failures)
        {
            foreach (var observation in _observations.Values.ToArray())
            {
                if (observation.Consumed) continue;
                try
                {
                    var error = await TakeOutcomeAsync(observation).ConfigureAwait(false);
                    if (error is not null && observation.ReportUnobserved) failures.Add(error);
                }
                catch (Exception error) { failures.Add(error); }
            }
        }

        private bool AllObserved => _observations.Values.All(item => item.Source.IsCompleted && item.Consumed);

        private async Task CleanupAsync(List<Exception> failures)
        {
            Source.TrySetResult(false);
            DisposeRelease.TrySetResult();
            Observe(Source.Task, reportUnobserved: FinishTask is null);
            if (FinishTask is not null) Observe(FinishTask, reportUnobserved: true);
            // The exact tail outcome is owned by Finish fallback or the strict supervisor.
            // This observation joins that root only; it must not report the same outcome twice.
            if (Owner.TrackedTask is { } tail) Observe(tail, reportUnobserved: false);
            try { Observe(Drain(), reportUnobserved: true); }
            catch (Exception error) { failures.Add(error); }
            await JoinUnobservedAsync(failures).ConfigureAwait(false);
            await JoinUnobservedAsync(failures).ConfigureAwait(false);
            var closed = AllObserved && Owner.Supervisor.CaptureSnapshot().ActiveTasks == 0;
            if (closed)
            {
                for (var index = _recordedObserved; index < Owner.Recorded.Count; index++)
                    failures.Add(Owner.Recorded[index].Failure);
                _recordedObserved = Owner.Recorded.Count;
                try { Enumerator.DisposeRegistration(); }
                catch (Exception error) { failures.Add(error); }
                try { Lifetime.Dispose(); }
                catch (Exception error) { failures.Add(error); }
                if (Session is not null)
                {
                    try
                    {
                        _sessionDispose ??= Session.DisposeAsync().AsTask();
                        Observe(_sessionDispose, reportUnobserved: true);
                    }
                    catch (Exception error) { failures.Add(error); closed = false; }
                    await JoinUnobservedAsync(failures).ConfigureAwait(false);
                    await JoinUnobservedAsync(failures).ConfigureAwait(false);
                    closed &= AllObserved && _sessionDispose is not null &&
                        _observations[_sessionDispose].Outcome.IsCompletedSuccessfully &&
                        _observations[_sessionDispose].Outcome.Result is null;
                }
                if (closed && _input is not null && _output is not null)
                {
                    try { _inputComplete ??= _input.Writer.CompleteAsync().AsTask(); Observe(_inputComplete, true); }
                    catch (Exception error) { failures.Add(error); closed = false; }
                    try { _outputComplete ??= _output.Reader.CompleteAsync().AsTask(); Observe(_outputComplete, true); }
                    catch (Exception error) { failures.Add(error); closed = false; }
                    await JoinUnobservedAsync(failures).ConfigureAwait(false);
                    await JoinUnobservedAsync(failures).ConfigureAwait(false);
                    closed &= AllObserved;
                }
            }
            if (!closed)
            {
                Quarantine.Add(this);
                failures.Add(new InvalidOperationException("Incomplete actual component owners remain rooted; resources were not torn down on waiter timeout."));
            }
        }

        private sealed class TaskObservation
        {
            internal TaskObservation(Task source, bool reportUnobserved)
            {
                Source = source;
                ReportUnobserved = reportUnobserved;
                Outcome = CaptureAsync(source);
            }
            internal Task Source { get; }
            internal Task<Exception?> Outcome { get; }
            internal bool ReportUnobserved { get; }
            internal bool Consumed { get; set; }
            private static async Task<Exception?> CaptureAsync(Task source)
            {
                try { await source.ConfigureAwait(false); return null; }
                catch (Exception error) { return error; }
            }
        }
    }

    private sealed class ComponentEnumerator(Task<bool> source, TaskCompletionSource entered, Task release, bool holdDispose)
        : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private CancellationToken _capturedToken;
        private CancellationTokenRegistration _callback;
        internal Exception? CallbackFailure, DisposeFailure;
        internal int DisposeCount;
        public int Current => throw new InvalidOperationException("No successful item is produced.");
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _capturedToken = cancellationToken;
            if (CallbackFailure is { } failure) _callback = cancellationToken.Register(() => throw failure);
            return this;
        }
        public ValueTask<bool> MoveNextAsync() => new(source);
        public ValueTask DisposeAsync()
        {
            Ensure(source.IsCompleted, "Dispose must not overlap the original MoveNext operation.");
            DisposeCount++;
            entered.TrySetResult();
            if (holdDispose) return new ValueTask(DisposeAfterReleaseAsync());
            return DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
        }
        private async Task DisposeAfterReleaseAsync()
        {
            await release.ConfigureAwait(false);
            if (DisposeFailure is { } failure) throw PhysicalTaskFailureException.From(failure);
        }
        internal void DisposeRegistration() => _callback.Dispose();
        internal void RequireCapturedLifetimeDisposed()
        {
            try { _ = _capturedToken.WaitHandle; }
            catch (ObjectDisposedException) { return; }
            throw new InvalidOperationException("Actual Runtime lifetime CTS must be disposed before tail release.");
        }
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

    private static Task Finish(IAsyncEnumerator<int> enumerator, Task pending, CancellationTokenSource lifetime,
        IGeneratedStreamPhysicalOwner? owner, Exception? lifetimeFailure = null,
        GeneratedPendingOperationRole role = GeneratedPendingOperationRole.IndependentPhysical, long requestId = 0)
    {
        var method = typeof(RpcSession).GetMethod("FinishGeneratedEnumeratorAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(int));
        return ((ValueTask)method.Invoke(null, [enumerator, pending, lifetime, owner, true, lifetimeFailure, role, requestId])!).AsTask();
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
        internal FrameworkTaskRegistration? AcceptedTrackingReceipt;
        internal InvocationReceiptHook? InvocationHook;
        internal int InvocationAttempts;
        public void TryAcceptInvocationOutcome(Task operation, long expectedRequestId,
            ref GeneratedInvocationOutcomeReceipt? receipt)
        {
            InvocationAttempts++;
            InvocationHook?.Invoke(operation, expectedRequestId, ref receipt);
        }
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
            {
                Supervisor.TrackStrictPhysical(task, "actual-generated-tail", ref registration);
                AcceptedTrackingReceipt = registration;
            }
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
