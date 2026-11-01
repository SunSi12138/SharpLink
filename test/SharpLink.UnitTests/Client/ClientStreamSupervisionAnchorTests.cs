using System.Linq;
using System.Buffers.Binary;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

// Source-only controls over actual Client group/pending/Session/supervisor owners. Manual Session
// fixtures are explicitly not pool-owned connection/timer census controls or full TCP evidence.
public sealed class ClientStreamSupervisionAnchorTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task SealedClientRejectsAnchorBeforePendingOrPhysicalStart()
    {
        await using var f = new Fixture();
        await f.Client.StopAsync();
        var group = new ClientStreamPublicationGroup(1);
        var failure = Capture(() => group.AdmitSupervision(f.Client, "rejected producing invocation"));
        Ensure(failure is InvalidOperationException && !group.IsAdopted && f.Connection.PendingCalls.ActiveCount == 0,
            "prestart rejection cannot publish pending/head or invent an adopted Session");
        Ensure(f.Client.FrameworkTaskSnapshotForDiagnostics.ActiveTasks == 0, "rejected anchor did not become a background orphan");
    }

    [Test]
    public async Task SynchronousOriginCompletionCannotFinishBeforeStrongAssignment()
    {
        await using var f = new Fixture();
        var group = f.NewGroup();
        group.MarkNoPending();
        Ensure(!group.SupervisionTaskForDiagnostics.IsCompleted, "no-pending receipt alone cannot finish unassigned origin");
        var origin = Task.CompletedTask; // Actual origin already exited before caller receives its Task.
        group.AttachOriginTask(origin);
        await group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
        await f.Client.StopAsync();
        Ensure(f.Client.FrameworkTaskSnapshotForDiagnostics.ActiveTasks == 0, "exact anchor registration joins and retires");
    }

    [Test]
    public async Task AlreadyCancelledRegistrationAdoptsOriginalIdentityBeforeTerminalCallbacks()
    {
        await using var f = new Fixture();
        f.Cancel.Cancel();
        var group = f.NewGroup();
        var operation = f.Register(group);
        Ensure(group.IsAdopted && group.RequestId > 0 && group.ProducerToken.IsCancellationRequested,
            "winning slot publishes group/producer lease before synchronous cancellation terminal");
        Ensure(await Error(operation.AsValueTask().AsTask()) is OperationCanceledException, "original operation observed exactly once");
        var cleanup = group.FinishAsync();
        group.AttachOriginTask(cleanup);
        await group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
        f.AssertReleased();
    }

    [Test]
    public async Task PublishedDeadlineRegistrationThrowRecoversOriginalOperationWithoutOrphan()
    {
        var clock = new RegistrationClock();
        await using var f = new Fixture(clock: clock);
        var group = f.NewGroup();
        var failure = new IOException("actual deadline scheduler timestamp failure after winning slot CAS");
        var deadline = RpcDeadline.Create(Limit, clock);
        clock.OnTimestamp = () => throw failure;
        var observed = Capture(() => f.Register(group, deadline));
        Ensure(ReferenceEquals(observed, failure) && group.IsAdopted, "real deadline callback is published=true, never NoPending");
        var original = group.TakeRegisteredOperation<int>();
        Ensure(original is not null && ReferenceEquals(await Error(original!.AsValueTask().AsTask()), failure),
            "registrar preserved original operation despite throwing postpublication callback");
        group.AttachOriginTask(group.FinishAsync()); // Setup stage is caller-owned, no duplicate writer candidate.
        await group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
        f.AssertReleased();
    }

    [Test]
    public async Task EarlierCancellationThenSetupThrowKeepsReceiptAndCallerStageSeparate()
    {
        var clock = new RegistrationClock();
        await using var f = new Fixture(clock: clock);
        var group = f.NewGroup();
        var failure = new IOException("setup stage after an earlier immutable cancellation");
        var deadline = RpcDeadline.Create(Limit, clock);
        clock.OnTimestamp = () => { f.Cancel.Cancel(); throw failure; };
        Ensure(ReferenceEquals(Capture(() => f.Register(group, deadline)), failure), "caller owns actual setup failure");
        var original = group.TakeRegisteredOperation<int>();
        Ensure(original is not null && await Error(original!.AsValueTask().AsTask()) is OperationCanceledException,
            "reentrant earlier cancellation remains original operation result");
        group.AttachOriginTask(group.FinishAsync());
        await group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
        await f.Client.StopAsync();
        Ensure(f.Client.FrameworkTaskSnapshotForDiagnostics.RetainedFailures == 0,
            "same setup stage is not also replayed into strict physical supervision");
        f.AssertReleased();
    }

    [Test]
    public async Task CallerOwnedSetupFailureDoesNotMaskIndependentPhysicalOwnerDisposal()
    {
        var clock = new RegistrationClock();
        await using var f = new Fixture(clock: clock);
        var group = f.NewGroup();
        var setup = new IOException("caller-owned setup stage");
        var cleanup = new OperationCanceledException("independent physical owner source disposal");
        var deadline = RpcDeadline.Create(Limit, clock);
        clock.OnTimestamp = () => throw setup;
        Ensure(ReferenceEquals(Capture(() => f.Register(group, deadline)), setup), "actual setup error remains caller-owned");
        var original = group.TakeRegisteredOperation<int>();
        Ensure(ReferenceEquals(await Error(original!.AsValueTask().AsTask()), setup), "original operation consumed once");
        // Actual physical-owner disposal path; default module lease makes no module-count claim.
        var owner = new SharpLinkClientStreamModuleLeaseOwner(default, new ThrowingOwnerSource(cleanup));
        group.TakeProducerOwner(owner); owner.Dispose();
        group.AttachOriginTask(group.FinishAsync());
        f.StopResultObserved = true;
        Ensure(ReferenceEquals(await Error(f.Client.StopAsync().AsTask()), cleanup),
            "only independent cleanup OCE reaches strict Stop, without setup replay or masking");
        f.AssertReleased();
    }

    [Test]
    public async Task NormalProducerReleasesCapacityWhileAnchorWaitsForRealLogicalReceipt()
    {
        await using var f = new Fixture();
        var group = f.NewGroup();
        var operation = f.Register(group);
        _ = group.TakeRegisteredOperation<int>();
        await f.PublishHead(group);
        var physical = group.RunWriterAsync(new EmptyWriter(f.Codec), group.ProducerToken);
        group.AttachOriginTask(physical);
        await physical.WaitAsync(Limit);
        Ensure(!group.SupervisionTaskForDiagnostics.IsCompleted && f.Connection.CallAdmissionReservationCount == 0,
            "normal producer never waits future response; supervision remains for original logical receipt");
        await WaitUntil(() => !group.HasOriginRootForDiagnostics);
        Ensure(!group.HasOriginRootForDiagnostics && group.ProducerTaskRootCountForDiagnostics == 0,
            "completed producer Task/payload roots are cleared after raw observation, before future receipt; this is not a heap/unload proof");
        f.Cancel.Cancel();
        Ensure(await Error(operation.AsValueTask().AsTask()) is OperationCanceledException, "original consumer sees logical cancel");
        await group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
        f.AssertReleased();
    }

    [Test]
    public Task HeadCancellationAfterSupervisorSealTransfersRootToHeldPhysicalFence()
        => HeadOwnershipCase(injectOutputFailure: false);


    [Test]
    public Task FailedHeadTaskTransfersToExactCleanupWithoutLosingPhysicalFailure()
        => HeadOwnershipCase(injectOutputFailure: true);

    private static Task HeadOwnershipCase(bool injectOutputFailure)
        => RunFixtureCaseAsync(new Fixture(holdOutput: true), async f =>
        {
            var supervisor = ActualSupervisor(f.Client);
            var group = f.NewGroup();
            RpcRequestOperation<int>? operation = null;
            Task? head = null;
            Task? drain = null;
            var physicalFailure = new IOException("actual head physical output failure");
            Exception? headOutcome = null, physicalOutcome = null, pendingOutcome = null;
            var failures = new List<Exception>();
            try
            {
                operation = f.Register(group);
                _ = group.TakeRegisteredOperation<int>();
                head = f.PublishHead(group, observeEmission: true);
                group.RetainHeadTask(head);
                await f.Output.Entered.Task.WaitAsync(Limit);
                if (injectOutputFailure)
                {
                    f.Output.Failure = physicalFailure;
                    f.Output.Release.TrySetResult();
                }
                else
                {
                    // Admission Seal ONLY. Actual Client.Stop closes binding Sessions and is
                    // deliberately not a substitute for this controlled supervision boundary.
                    supervisor.Seal(); drain = supervisor.DrainAsync();
                    Ensure(f.Client.FrameworkTaskSnapshotForDiagnostics.IsSealed && group.HasOriginRootForDiagnostics,
                        "actual supervisor Seal preserves the accepted startup root");
                    f.Cancel.Cancel();
                }
                headOutcome = await ObserveOutcome(head);
                if (headOutcome is TimeoutException timeout)
                    AddStage(failures, "initial head observation deadline", timeout);
            }
            catch (Exception error) { AddStage(failures, "body before physical handoff", error); }
            finally
            {
                // No assertion may skip actual head exit or the one cleanup adoption. Even a
                // surprising logical head result must not leave the preadmitted anchor unassigned.
                if (injectOutputFailure || head is null || !head.IsCompleted) f.Output.Release.TrySetResult();
                if (head is not null && !head.IsCompleted) headOutcome = await ObserveOutcome(head);
                if (head is null || head.IsCompleted)
                {
                    if (group.IsAdopted)
                    {
                        var terminal = Capture(() => f.Connection.PendingCalls.CompleteProducingSetupFailure(group,
                            headOutcome ?? new OperationCanceledException(group.ProducerToken)));
                        if (terminal is not null) AddStage(failures, "pending terminalization", terminal);
                    }
                    var cleanup = group.FinishAsync(); // No injected OR forwarded head error: physical oracle comes from actual cleanup/fence.
                    var assignment = Capture(() => group.AttachOriginTask(cleanup));
                    if (assignment is not null) AddStage(failures, "cleanup assignment", assignment);
                    if (!injectOutputFailure)
                    {
                        var assertion = Capture(() => Ensure(!cleanup.IsCompleted && drain is { IsCompleted: false } &&
                            f.Connection.PendingCalls.ActiveCount == 1,
                            "real held output/fence keeps original physical pending charge and anchor after Seal"));
                        if (assertion is not null) AddStage(failures, "held physical barrier", assertion);
                    }
                    f.Output.Release.TrySetResult();
                    physicalOutcome = await ObserveOutcome(cleanup);
                }
                else AddStage(failures, "head physical join", new TimeoutException("Original head still owns its root; no proxy origin was attached."));
                if (operation is not null) pendingOutcome = await ObserveOutcome(operation.AsValueTask().AsTask());
            }
            supervisor.Seal();
            drain ??= supervisor.DrainAsync();
            var supervisionOutcome = await ObserveOutcome(drain);
            var assertions = Capture(() =>
            {
                if (injectOutputFailure)
                {
                    Ensure(headOutcome is OperationCanceledException canceled && canceled.CancellationToken == group.ProducerToken ||
                        OnlyInjectedPhysicalCause(headOutcome, physicalFailure),
                        "logical emission wait must be the original producer cancellation or actual physical failure");
                    Ensure(OnlyInjectedPhysicalCause(pendingOutcome, physicalFailure), "actual pending emission observer retains injected physical IOException");
                    Ensure(OnlyInjectedPhysicalCause(physicalOutcome, physicalFailure), "real cleanup/fence, not a supplied expected candidate, retains physical IOException");
                    Ensure(OnlyInjectedPhysicalCause(supervisionOutcome, physicalFailure), "strict supervisor retains only actual injected physical fault occurrences");
                }
                else
                {
                    Ensure(headOutcome is OperationCanceledException && pendingOutcome is OperationCanceledException,
                        "original head and pending operation observe logical cancellation once");
                    Ensure(physicalOutcome is null && supervisionOutcome is null, "Seal alone does not fabricate Session-close/fence failures");
                }
                Ensure(f.Client.FrameworkTaskSnapshotForDiagnostics.ActiveTasks == 0, "no orphaned original registration");
                f.AssertReleased();
            });
            if (assertions is not null) AddStage(failures, "post-join provenance assertions", assertions);
            // Unexpected raw outcomes are preserved even if an assertion above fails first.
            if (headOutcome is not null && !(headOutcome is OperationCanceledException originalCancellation && originalCancellation.CancellationToken == group.ProducerToken) &&
                (!injectOutputFailure || !OnlyInjectedPhysicalCause(headOutcome, physicalFailure)))
                AddStage(failures, "logical head observation", headOutcome);
            if (pendingOutcome is not null && (injectOutputFailure
                ? !OnlyInjectedPhysicalCause(pendingOutcome, physicalFailure) : pendingOutcome is not OperationCanceledException))
                AddStage(failures, "original pending operation", pendingOutcome);
            if (physicalOutcome is not null && (!injectOutputFailure || !OnlyInjectedPhysicalCause(physicalOutcome, physicalFailure)))
                AddStage(failures, "physical cleanup", physicalOutcome);
            if (supervisionOutcome is not null && (!injectOutputFailure || !OnlyInjectedPhysicalCause(supervisionOutcome, physicalFailure)))
                AddStage(failures, "supervisor drain", supervisionOutcome);
            ThrowStages(failures);
        });


    [Test]
    public async Task OneWayPhysicalFinishDoesNotAwaitItsOwnLocalStreamComplete()
    {
        await using var f = new Fixture();
        var group = f.NewGroup();
        Ensure(f.Connection.TryReserveExactCallAdmission(), "original selection");
        group.PrepareRegistration(f.Connection, new TestRpcBindingPublication(f.Session));
        var registration = f.Connection.PendingCalls.RegisterOneWayClientStream(default, f.Cancel.Token, producerGroup: group);
        _ = group.TakeRegisteredOperation<RpcEmptyRequest>();
        await f.PublishHead(group, oneWay: true);
        var physical = group.RunWriterAsync(new EmptyWriter(f.Codec), group.ProducerToken);
        group.AttachOriginTask(physical);
        await physical.WaitAsync(Limit);
        Ensure(!group.SupervisionTaskForDiagnostics.IsCompleted, "anchor, not producer, waits LocalStreamComplete");
        f.Connection.PendingCalls.TryComplete(registration.Id, PendingCallCompletionReason.LocalStreamComplete);
        await registration.Operation.AsValueTask();
        await group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
        f.AssertReleased();
    }

    [Test]
    public Task ResponseDrainExtendsExistingAnchorAfterSupervisorSealAndKeepsRawCleanupError()
        => RunFixtureCaseAsync(new Fixture(), async f =>
        {
            var supervisor = ActualSupervisor(f.Client);
            var group = f.NewGroup();
            var expected = new OperationCanceledException("independent response buffer retirement");
            var dispatcher = new HeldDispatcher(expected);
            var failures = new List<Exception>();
            Task? physical = null, dispatch = null, drain = null;
            try
            {
                Ensure(f.Connection.TryReserveExactCallAdmission(), "original selection");
                group.PrepareRegistration(f.Connection, new TestRpcBindingPublication(f.Session));
                var id = f.Connection.PendingCalls.RegisterStream(PendingCallKind.DuplexStreaming, dispatcher, default,
                    f.Cancel.Token, producerGroup: group);
                f.Session.StreamManager.Register(id, 0, dispatcher);
                await f.PublishHead(group);
                physical = group.RunWriterAsync(new EmptyWriter(f.Codec), group.ProducerToken);
                group.AttachOriginTask(physical);
                await physical.WaitAsync(Limit);
                dispatch = f.Session.StreamManager.DispatchChunkAsync(id, 0, ReadOnlySequence<byte>.Empty).AsTask();
                await dispatcher.Entered.Task.WaitAsync(Limit);
                supervisor.Seal(); drain = supervisor.DrainAsync();
                Ensure(f.Client.FrameworkTaskSnapshotForDiagnostics.IsSealed, "original anchor admitted before actual supervisor Seal");
                f.Cancel.Cancel();
                Ensure(group.HasResponseDrainRootForDiagnostics && !drain.IsCompleted,
                    "void terminal extends accepted anchor while original route dispatch is still held; no fresh post-Seal admission");
            }
            catch (Exception error) { AddStage(failures, "body/cancellation callback", error); }
            finally
            {
                dispatcher.Release.TrySetResult();
                var cancellation = Capture(() => f.Cancel.Cancel());
                if (cancellation is not null) AddStage(failures, "final pending cancellation", cancellation);
                if (physical is null)
                {
                    physical = group.FinishAsync();
                    var assignment = Capture(() => group.AttachOriginTask(physical));
                    if (assignment is not null) AddStage(failures, "physical assignment", assignment);
                }
                var physicalError = await ObserveOutcome(physical);
                if (physicalError is not null) AddStage(failures, "producer physical task", physicalError);
                if (dispatch is not null)
                {
                    var dispatchError = await ObserveOutcome(dispatch);
                    if (dispatchError is not null) AddStage(failures, "acquired response dispatch", dispatchError);
                }
            }
            supervisor.Seal(); drain ??= supervisor.DrainAsync();
            var terminal = await ObserveOutcome(drain);
            if (!ReferenceEquals(terminal, expected))
                AddStage(failures, "strict response cleanup outcome", terminal ?? new InvalidOperationException("Missing exact independent retirement OCE."));
            var assertion = Capture(() =>
            {
                Ensure(f.Client.FrameworkTaskSnapshotForDiagnostics.ActiveTasks == 0, "anchor exits after real response cleanup");
                f.AssertReleased();
            });
            if (assertion is not null) AddStage(failures, "post-drain assertions", assertion);
            ThrowStages(failures);
        });


    [Test]
    public Task CancellationBeforeActualRoutePublishDrainsLateOriginalRoute()
        => RoutePublicationRace(0);
    [Test]
    public Task ConsumerAbandonmentObserverThrowStillDrainsLateOriginalRoute()
        => RoutePublicationRace(1, observerThrows: true);
    [Test]
    public Task ConnectionClosureBeforeActualRoutePublishStillDrainsLateOriginalRoute()
        => RoutePublicationRace(2);
    [Test]
    public Task RemoteTerminalBeforeActualRoutePublishStillDrainsLateOriginalRoute()
        => RoutePublicationRace(3);
    [Test]
    public Task NeverAttemptedRoutePublicationSettlesDeferredTerminalWithoutLateRegistration()
        => RoutePublicationRace(0, neverPublish: true);
    [Test]
    public Task ActualRegisterThrowSettlesPhaseWithoutOrphaningDeferredTerminal()
        => RoutePublicationRace(0, registrationThrows: true);

    [Test]
    public Task ConnectionClosureDuringAcquiredDispatchKeepsExactDrainOwner()
        => NonCancellationTerminalDuringDispatch(PendingCallCompletionReason.ConnectionClosed);
    [Test]
    public Task RemoteTerminalDuringAcquiredDispatchKeepsExactDrainOwner()
        => NonCancellationTerminalDuringDispatch(PendingCallCompletionReason.RemoteStreamComplete);

    private static async Task NonCancellationTerminalDuringDispatch(PendingCallCompletionReason reason)
    {
        await using var f = new Fixture();
        var group = f.NewGroup();
        var dispatcher = new HeldDispatcher(new IOException("noncancel path must not retire a local-abort buffer"));
        Ensure(f.Connection.TryReserveExactCallAdmission(), "original selection");
        group.PrepareRegistration(f.Connection, new TestRpcBindingPublication(f.Session));
        var id = f.Connection.PendingCalls.RegisterStream(PendingCallKind.DuplexStreaming, dispatcher,
            default, f.Cancel.Token, producerGroup: group);
        f.Session.StreamManager.Register(id, 0, dispatcher);
        var dispatch = f.Session.StreamManager.DispatchChunkAsync(id, 0, ReadOnlySequence<byte>.Empty).AsTask();
        await dispatcher.Entered.Task.WaitAsync(Limit);
        try
        {
            f.Connection.PendingCalls.TryComplete(id, reason);
            group.AttachOriginTask(group.FinishAsync());
            Ensure(group.HasResponseDrainRootForDiagnostics && !group.SupervisionTaskForDiagnostics.IsCompleted,
                "noncancel terminal owns held real dispatch without evaluating a cancel-only enum mapping");
            dispatcher.Release.TrySetResult();
            await dispatch.WaitAsync(Limit);
            await group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
            Ensure(f.Session.StreamManager.ActiveStreamCount == 0, "exact dispatch route removed after physical exit");
            f.AssertReleased();
        }
        finally { dispatcher.Release.TrySetResult(); await Error(dispatch); }
    }

    [Test]
    public Task PermanentClockAfterSerializationFailureSettlesClientStreamingHead()
        => SerializationFailureWithPermanentClock(PendingCallKind.ClientStreaming);
    [Test]
    public Task PermanentClockAfterSerializationFailureSettlesOneWayStreamingHead()
        => SerializationFailureWithPermanentClock(PendingCallKind.OneWayClientStreaming);
    [Test]
    public Task PermanentClockAfterSerializationFailureSettlesDuplexHead()
        => SerializationFailureWithPermanentClock(PendingCallKind.DuplexStreaming);

    [Test]
    public async Task KnownDeadlineAndThrowingTerminalObserverStillResolveHeadWithoutClockResample()
    {
        var clock = new RegistrationClock();
        await using var f = new Fixture(clock: clock);
        var group = f.NewGroup();
        var deadlineFailure = new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "already selected original deadline");
        var independent = new IOException("actual terminal observer after exact head refusal");
        var recording = new RecordingTerminalObserver(independent);
        Ensure(f.Connection.TryReserveExactCallAdmission(), "original selection");
        group.PrepareRegistration(f.Connection, new TestRpcBindingPublication(f.Session));
        var operation = f.Connection.PendingCalls.Rent(f.Codec, PendingCallKind.ClientStreaming,
            RpcDeadline.Create(Limit, clock), f.Cancel.Token, out _, recording, producerGroup: group);
        _ = group.TakeRegisteredOperation<int>();
        clock.PermanentFailure = new IOException("permanent provider failure must not replace known deadline");
        var samples = clock.TimestampCalls;
        try
        {
            Ensure(ReferenceEquals(SharpLinkClient.CompleteFailedProducingPending(f.Connection, group, deadlineFailure), deadlineFailure),
                "accepted terminal-batch ownership does not replace original known error");
            Ensure(recording.Reason == PendingCallCompletionReason.DeadlineExceeded && ReferenceEquals(recording.Error, deadlineFailure),
                "known deadline reason and exact original error retained");
            Ensure(ReferenceEquals(await Error(operation.AsValueTask().AsTask()), deadlineFailure), "original operation still delivered despite observer throw");
            group.AttachOriginTask(group.FinishAsync(deadlineFailure));
            f.StopResultObserved = true;
            Ensure(ReferenceEquals(await Error(f.Client.StopAsync().AsTask()), independent),
                "independent terminal observer failure belongs to strict physical owner, never lost behind head wait");
            Ensure(clock.TimestampCalls == samples && !group.HeadPublicationConfirmed, "head refusal finalized before fallible terminal callbacks");
            f.AssertReleased();
        }
        finally { clock.PermanentFailure = null; }
    }

    private static async Task SerializationFailureWithPermanentClock(PendingCallKind kind)
    {
        var clock = new RegistrationClock();
        await RunFixtureCaseAsync(new Fixture(clock: clock), async f =>
        {
            var group = f.NewGroup();
            var expected = new IOException("actual request serialization failed before head publication");
            var clockFailure = new IOException("original provider remains broken throughout terminal cleanup");
            var deadline = RpcDeadline.Create(Limit, clock);
            var observer = new RecordingTerminalObserver();
            var dispatcher = kind == PendingCallKind.DuplexStreaming ? PooledAsyncStreamDispatcher<int>.Rent(default, f.Codec) : null;
            var lease = dispatcher?.RetainForRegistration() ?? 0;
            var leaseOwned = dispatcher is not null;
            var failures = new List<Exception>();
            Ensure(f.Connection.TryReserveExactCallAdmission(), "original selection");
            if (dispatcher is not null) group.BeginResponseRoutePublication();
            group.PrepareRegistration(f.Connection, new TestRpcBindingPublication(f.Session), lease);
            Func<Task> observe;
            if (dispatcher is not null)
            {
                var id = f.Connection.PendingCalls.RegisterStream(kind, dispatcher, deadline, f.Cancel.Token, observer, group);
                try { f.Session.StreamManager.Register(id, 0, dispatcher); }
                finally { group.CompleteResponseRoutePublication(); }
                observe = () => dispatcher.MoveNextAsync().AsTask();
            }
            else if (kind == PendingCallKind.OneWayClientStreaming)
            {
                var registration = f.Connection.PendingCalls.RegisterOneWayClientStream(deadline, f.Cancel.Token, observer, group);
                _ = group.TakeRegisteredOperation<RpcEmptyRequest>();
                observe = () => registration.Operation.AsValueTask().AsTask();
            }
            else
            {
                var operation = f.Connection.PendingCalls.Rent(f.Codec, kind, deadline, f.Cancel.Token, out _,
                    completionObserver: observer, producerGroup: group);
                _ = group.TakeRegisteredOperation<int>();
                observe = () => operation.AsValueTask().AsTask();
            }
            var local = new RpcLocalContract(Guid.NewGuid(), 1, 1, new RpcHash128(1, 1));
            var site = new SharpLinkClient.ContractCallSite(f.Client, local, new RpcProxyOptions(), new ClientLocalContractControl(local));
            var codec = new BreakClockRequestCodec(clock, clockFailure, expected);
            var error = Capture(() => site.SendRpcCall(f.Session, group, 1, group.RequestId,
                kind == PendingCallKind.OneWayClientStreaming ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.Cancellable,
                7, codec, deadline, null, publicationTable: f.Connection.PendingCalls));
            var samples = clock.TimestampCalls;
            var terminal = SharpLinkClient.CompleteFailedProducingPending(f.Connection, group,
                error ?? new InvalidOperationException("Expected actual serializer failure was missing."));
            async Task Origin()
            {
                Exception? failure = null;
                try { await group.FinishAsync(terminal); }
                catch (Exception physical) { failure = PhysicalTaskFailureException.GetRaw(physical); }
                finally
                {
                    // This is the original registration lifetime, not consumer Dispose. Match
                    // actual Duplex origin-finally: release AFTER physical finish, BEFORE sink read.
                    if (leaseOwned)
                    {
                        leaseOwned = false;
                        try
                        {
                            var owned = group.IsAdopted ? group.FinishOriginSinkUseForRetention() : lease;
                            if (owned != 0) dispatcher!.ReleaseRegistrationRetention(owned);
                        }
                        catch (Exception release) { failure = ClientStreamPublicationGroup.AddPhysical(failure, release); }
                    }
                }
                if (failure is not null) throw PhysicalTaskFailureException.From(failure);
            }
            var origin = Origin();
            group.AttachOriginTask(origin); // Before any assertion or terminal consumption can fail.
            try
            {
                var physical = await ObserveOutcome(origin);
                if (physical is not null) AddStage(failures, "serializer origin/registration release", physical);
                var anchor = await ObserveOutcome(group.SupervisionTaskForDiagnostics);
                if (anchor is not null) AddStage(failures, "serializer anchor", anchor);
                var pending = await ObserveOutcome(observe());
                Ensure(ReferenceEquals(error, expected) && codec.Calls == 1, "actual production codec failed with writer cleanup");
                Ensure(ReferenceEquals(terminal, expected) && clock.TimestampCalls == samples,
                    "actual shared failure helper and physical finish never resample permanently broken provider");
                Ensure(observer.Reason == PendingCallCompletionReason.SendFailure && ReferenceEquals(observer.Error, expected),
                    "exact original terminal reason/error preserved");
                Ensure(ReferenceEquals(pending, expected), "original sink is consumed once AFTER registration-origin release");
                Ensure(!group.HeadPublicationConfirmed, "head definitely refused before physical release");
                f.AssertReleased();
            }
            catch (Exception body) { AddStage(failures, "serialization body", body); }
            finally
            {
                // Join original physical Task independently of assertion outcome. Its finally
                // owns the single registration release; no early consumer disposal or double release.
                if (!origin.IsCompleted)
                {
                    var join = await ObserveOutcome(origin);
                    if (join is not null) AddStage(failures, "serializer final physical join", join);
                }
                clock.PermanentFailure = null;
                if (dispatcher is not null)
                {
                    var disposed = await ObserveOutcome(dispatcher.DisposeAsync().AsTask());
                    if (disposed is not null) AddStage(failures, "serializer consumer Dispose", disposed);
                }
            }
            ThrowStages(failures);
        });
    }

    [Test]
    public async Task ActualRouteFailureWithPermanentClockSettlesOriginalDuplexSetup()
    {
        var clock = new RegistrationClock();
        await RunFixtureCaseAsync(new Fixture(clock: clock), async f =>
        {
            var group = f.NewGroup();
            var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, f.Codec);
            var lease = dispatcher.RetainForRegistration();
            var observer = new RecordingTerminalObserver();
            var failures = new List<Exception>();
            Ensure(f.Connection.TryReserveExactCallAdmission(), "original selection");
            group.BeginResponseRoutePublication();
            group.PrepareRegistration(f.Connection, new TestRpcBindingPublication(f.Session), lease);
            var id = f.Connection.PendingCalls.RegisterStream(PendingCallKind.DuplexStreaming, dispatcher,
                RpcDeadline.Create(Limit, clock), f.Cancel.Token, observer, group);
            const long unrelatedRoute = 1_000_001;
            f.Session.StreamManager.Register(unrelatedRoute, 0, dispatcher);
            Exception? setup = null, terminalization = null;
            clock.PermanentFailure = new IOException("permanent original clock failure after registration");
            var samples = clock.TimestampCalls;
            async Task Origin()
            {
                Exception? failure = null;
                try
                {
                    try
                    {
                        using var publication = SharpLinkClient.ContractCallSite.PrepareDuplexResponseRoute(f.Connection, id, dispatcher, group);
                        publication.Publish();
                    }
                    catch (Exception error)
                    {
                        setup = error;
                        terminalization = SharpLinkClient.CompleteFailedProducingPending(f.Connection, group, error);
                    }
                    finally { group.CompleteResponseRoutePublication(); }
                    await group.FinishAsync(); // Setup belongs to caller, not replayed physical truth.
                }
                catch (Exception physical) { failure = PhysicalTaskFailureException.GetRaw(physical); }
                finally
                {
                    try
                    {
                        var owned = group.IsAdopted ? group.FinishOriginSinkUseForRetention() : lease;
                        if (owned != 0) dispatcher.ReleaseRegistrationRetention(owned);
                    }
                    catch (Exception release) { failure = ClientStreamPublicationGroup.AddPhysical(failure, release); }
                }
                if (failure is not null) throw PhysicalTaskFailureException.From(failure);
            }
            var origin = Origin(); group.AttachOriginTask(origin);
            try
            {
                var physical = await ObserveOutcome(origin);
                if (physical is not null) AddStage(failures, "route origin/registration release", physical);
                var anchor = await ObserveOutcome(group.SupervisionTaskForDiagnostics);
                if (anchor is not null) AddStage(failures, "route anchor", anchor);
                Ensure(setup is InvalidOperationException && ReferenceEquals(terminalization, setup) && ReferenceEquals(observer.Error, setup) &&
                    observer.Reason == PendingCallCompletionReason.SendFailure && clock.TimestampCalls == samples,
                    "exact route failure terminalized without resampling permanent clock");
                Ensure(ReferenceEquals(await ObserveOutcome(dispatcher.MoveNextAsync().AsTask()), setup),
                    "original stream terminal consumable after original registration-origin finally");
                Ensure(f.Session.StreamManager.ActiveStreamCount == 1, "exact cleanup preserved unrelated full-ID route");
                f.AssertReleased();
            }
            catch (Exception body) { AddStage(failures, "route body", body); }
            finally
            {
                if (!origin.IsCompleted)
                {
                    var join = await ObserveOutcome(origin);
                    if (join is not null) AddStage(failures, "route final physical join", join);
                }
                clock.PermanentFailure = null;
                var unregister = Capture(() => f.Session.StreamManager.Unregister(unrelatedRoute, 0));
                if (unregister is not null) AddStage(failures, "unrelated route fixture cleanup", unregister);
                var dispose = await ObserveOutcome(dispatcher.DisposeAsync().AsTask());
                if (dispose is not null) AddStage(failures, "route consumer Dispose", dispose);
            }
            ThrowStages(failures);
        });
    }

    private sealed class BreakClockRequestCodec(RegistrationClock clock, Exception clockFailure, Exception serializationFailure) : IRpcCodec<int>
    {
        internal int Calls;
        public void Serialize(in int value, IBufferWriter<byte> writer)
        { ++Calls; clock.PermanentFailure = clockFailure; throw serializationFailure; }
        public int Deserialize(in ReadOnlySequence<byte> value) => throw new NotSupportedException();
    }
    private sealed class RecordingTerminalObserver(Exception? failure = null) : IPendingCallCompletionObserver
    {
        internal PendingCallCompletionReason Reason;
        internal Exception? Error;
        public void OnResponseObserved() { }
        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        { Reason = completion.Reason; Error = completion.Exception; if (failure is not null) throw failure; }
    }

    private static async Task RoutePublicationRace(int terminal, bool observerThrows = false,
        bool neverPublish = false, bool registrationThrows = false)
    {
        await using var f = new Fixture();
        var group = f.NewGroup();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, f.Codec);
        var lease = dispatcher.RetainForRegistration();
        var expected = new IOException("actual abandonment observer during unpublished response route");
        var observer = observerThrows ? new ThrowingTerminalObserver(expected) : null;
        Ensure(f.Connection.TryReserveExactCallAdmission(), "original selection");
        group.BeginResponseRoutePublication();
        group.PrepareRegistration(f.Connection, new TestRpcBindingPublication(f.Session), lease);
        var id = f.Connection.PendingCalls.RegisterStream(PendingCallKind.DuplexStreaming, dispatcher,
            default, f.Cancel.Token, observer, group);
        const long unrelatedRoute = 1_000_000;
        if (registrationThrows) f.Session.StreamManager.Register(unrelatedRoute, 0, dispatcher);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? setupFailure = null;
        var consumerDisposeAttempted = false;
        async Task Origin()
        {
            try
            {
                // These are the exact production-used setup/publication steps. The test holds
                // BETWEEN them; no test callback/null branch or copied registration algorithm.
                using var publication = SharpLinkClient.ContractCallSite.PrepareDuplexResponseRoute(f.Connection, id, dispatcher, group);
                entered.TrySetResult();
                await release.Task;
                if (!neverPublish) publication.Publish();
            }
            catch (Exception error) { setupFailure = error; } // Caller owns setup, not physical replay.
            finally { group.CompleteResponseRoutePublication(); }
            try { await group.FinishAsync(); }
            finally
            {
                var owned = group.IsAdopted ? group.FinishOriginSinkUseForRetention() : lease;
                if (owned != 0) dispatcher.ReleaseRegistrationRetention(owned);
            }
        }
        var origin = Origin();
        group.AttachOriginTask(origin);
        await entered.Task.WaitAsync(Limit);
        try
        {
            if (terminal == 1)
            {
                consumerDisposeAttempted = true;
                await dispatcher.DisposeAsync().AsTask().WaitAsync(Limit);
            }
            else if (terminal == 2) f.Connection.PendingCalls.TryComplete(id, PendingCallCompletionReason.ConnectionClosed);
            else if (terminal == 3) f.Connection.PendingCalls.TryComplete(id, PendingCallCompletionReason.RemoteStreamComplete);
            else f.Cancel.Cancel();
            Ensure(group.HasResponseDrainRootForDiagnostics && !group.SupervisionTaskForDiagnostics.IsCompleted,
                "terminal publishes promptly but existing anchor waits actual route-publication decision");
            release.TrySetResult();
            var physical = await Error(origin);
            if (observerThrows)
            {
                Ensure(physical is PhysicalTaskFailureException && observer!.Calls == 1,
                    "actual throwing abandonment observer remains independently owned after route reconciliation");
                f.StopResultObserved = true;
                Ensure(ReferenceEquals(await Error(f.Client.StopAsync().AsTask()), expected), "strict owner retains exact observer error once");
            }
            else
            {
                Ensure(physical is null, "pure terminal/route race has no invented physical failure");
                await group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
            }
            Ensure(registrationThrows ? setupFailure is InvalidOperationException : setupFailure is null,
                "actual duplicate dispatcher-state registration failure remains caller setup outcome");
            Ensure(f.Session.StreamManager.ActiveStreamCount == (registrationThrows ? 1 : 0),
                "late exact route is gone; failed publication cannot delete unrelated original route");
            f.AssertReleased();
        }
        finally
        {
            f.Cancel.Cancel(); release.TrySetResult(); await Error(origin);
            if (registrationThrows) f.Session.StreamManager.Unregister(unrelatedRoute, 0);
            if (!consumerDisposeAttempted) await dispatcher.DisposeAsync();
        }
    }

    private sealed class ThrowingTerminalObserver(Exception failure) : IPendingCallCompletionObserver
    {
        internal int Calls;
        public void OnResponseObserved() { }
        public void OnPendingCallCompleted(in PendingCallCompletion completion) { ++Calls; throw failure; }
    }

    // Read the actual existing supervisor; no shipping test hook, field mutation, substitute
    // supervisor or Client.Stop side effect. These controls prove Seal, not full shutdown.
    private static FrameworkTaskSupervisor ActualSupervisor(SharpLinkClient client)
        => (FrameworkTaskSupervisor)(typeof(SharpLinkClient).GetField("_frameworkTasks",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(client)
            ?? throw new InvalidOperationException("Actual Client supervisor field was not available."));

    private static bool OnlyInjectedPhysicalCause(Exception? error, Exception injected)
        => ReferenceEquals(error, injected) || (error switch
        {
            PhysicalTaskFailureException physical => OnlyInjectedPhysicalCause(physical.RawException, injected),
            AggregateException aggregate => aggregate.InnerExceptions.Count != 0 &&
                aggregate.InnerExceptions.All(item => OnlyInjectedPhysicalCause(item, injected)),
            SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed, InnerException: { } inner }
                => OnlyInjectedPhysicalCause(inner, injected),
            _ => false
        });

    private static async Task<Exception?> ObserveOutcome(Task task)
    {
        try { await task.WaitAsync(Limit); return null; }
        catch (Exception error) { return error; } // Outcome recorded/validated below, never blanket success.
    }
    private static void AddStage(List<Exception> failures, string stage, Exception error)
        => failures.Add(new InvalidOperationException("Fixture stage: " + stage, error));
    private static void ThrowStages(List<Exception> failures)
    {
        if (failures.Count != 0) throw new AggregateException("Independent body/owner/teardown outcomes (raw identities retained).", failures);
    }
    private static async Task AttemptStageAsync(List<Exception> failures, string stage, Func<Task> action)
    {
        Exception? outcome;
        try { outcome = await ObserveOutcome(action()); }
        catch (Exception synchronous) { outcome = synchronous; }
        if (outcome is not null) AddStage(failures, stage, outcome);
    }
    private static async Task RunFixtureCaseAsync(Fixture fixture, Func<Fixture, Task> body)
    {
        var failures = new List<Exception>();
        try { await body(fixture); }
        catch (Exception error) { AddStage(failures, "body", error); }
        // No await-using replacement of an earlier body failure by a later Stop failure.
        // These selected cases join their physical origins before this independent teardown.
        await fixture.DisposeObservedAsync(failures);
        ThrowStages(failures);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= Limit) throw new TimeoutException("Actual owner phase did not settle.");
            await Task.Yield();
        }
    }
    private static bool Contains(Exception? error, Exception expected)
        => ReferenceEquals(error, expected) || error is AggregateException aggregate && aggregate.InnerExceptions.Any(e => Contains(e, expected)) ||
            error?.InnerException is { } inner && Contains(inner, expected);
    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception e) { return e; } }
    private static async Task<Exception?> Error(Task task)
    { try { await task.WaitAsync(Limit); return null; } catch (Exception e) when (e is not TimeoutException) { return e; } }
    private static void Ensure(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private readonly struct EmptyWriter(IRpcCodec<int> codec) : IRpcClientStreamWriter
    { public ValueTask WriteAsync(IRpcClientStreamSink sink, long id, CancellationToken token = default) => new(sink.SendClientStreamAsync(id, 1, new EmptyStream(), codec, token)); }
    private sealed class EmptyStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    { public int Current => 0; public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken token = default) => this; public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class RegistrationClock : TimeProvider
    {
        private readonly ManualTimeProvider _inner = new();
        internal Action? OnTimestamp;
        internal Exception? PermanentFailure;
        internal int TimestampCalls;
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp()
        {
            ++TimestampCalls;
            if (PermanentFailure is { } failure) throw failure;
            var callback = Interlocked.Exchange(ref OnTimestamp, null);
            callback?.Invoke(); // Disarm before reentrant cancellation samples original clock.
            return _inner.GetTimestamp();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            => _inner.CreateTimer(callback, state, due, period);
    }
    private sealed class ThrowingOwnerSource(Exception failure) : CancellationTokenSource
    { protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) throw failure; } }
    private sealed class HeldDispatcher(Exception retirementFailure) : IStreamDispatcher, IStreamLocalAbortDispatcher
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) { Entered.TrySetResult(); return new(Release.Task); }
        public void Complete(bool isError, string? message) { }
        public void Complete(Exception? error) { }
        public void CompleteLocalAbort(Exception? error) { }
        public void RetireLocalAbortBuffer() => throw retirementFailure;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly System.IO.Pipelines.Pipe _input = new();
        private readonly System.IO.Pipelines.Pipe _output = new();
        internal readonly SharpLinkClient Client;
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly RpcSession Session;
        internal readonly ClientConnection Connection;
        internal readonly IRpcCodec<int> Codec;
        internal readonly HeldOutput Output;
        internal bool StopResultObserved;
        internal Fixture(bool holdOutput = false, TimeProvider? clock = null)
        {
            Client = ClientBuilderTestHelper.Build(new TestClientTransportFactory(), builder => { if (clock is not null) builder.UseTimeProvider(clock); });
            var context = (SharpLinkRuntimeContext)Client.RuntimeContext;
            Output = new HeldOutput(_output.Writer, holdOutput);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport("client-anchor", _input.Reader, Output, RpcSessionTestFixture.ClientOptions(context));
            Session.NotifyConnected();
            Connection = new ClientConnection(Client, Session, new CancellationTokenSource(), 8, context);
            Codec = context.Codecs.GetCodec<int>();
        }
        internal ClientStreamPublicationGroup NewGroup()
        { var group = new ClientStreamPublicationGroup(1); group.AdmitSupervision(Client, "actual producing group control"); return group; }
        internal RpcRequestOperation<int> Register(ClientStreamPublicationGroup group, RpcDeadline deadline = default)
        {
            Ensure(Connection.TryReserveExactCallAdmission(), "original selection reservation");
            group.PrepareRegistration(Connection, new TestRpcBindingPublication(Session));
            return Connection.PendingCalls.Rent(Codec, PendingCallKind.ClientStreaming, deadline, Cancel.Token,
                out _, producerGroup: group);
        }
        internal Task PublishHead(ClientStreamPublicationGroup group, bool observeEmission = false, bool oneWay = false)
        {
            var writer = Session.RentFrameWriter();
            using (writer.BeginPacketScope(oneWay ? ProtocolV2FrameType.OneWayStreamRequest : ProtocolV2FrameType.Request,
                oneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.Cancellable, (ulong)group.RequestId))
            {
                var span = writer.GetSpan(oneWay ? 18 : 16);
                BinaryPrimitives.WriteUInt64LittleEndian(span, group.Binding.Token);
                BinaryPrimitives.WriteInt64LittleEndian(span[8..], 1);
                if (oneWay) BinaryPrimitives.WriteUInt16LittleEndian(span[16..], 1);
                writer.Advance(oneWay ? 18 : 16);
            }
            var prepared = Session.PrepareOutboundFrame(writer, group.ProducerToken);
            Ensure(Connection.PendingCalls.TryPublishRequest(group.RequestId, Session, prepared, observeEmission, group.ProducerToken,
                Connection.PendingCalls, out var emission, out _, group), "actual head transaction");
            if (!oneWay) Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId);
            return observeEmission ? emission.AsTask() : Session.FlushSendQueueAsync().AsTask();
        }
        internal void AssertReleased() => Ensure(Connection.PendingCalls.ActiveCount == 0 && Connection.CallAdmissionReservationCount == 0 && Session.QueuedSendBytes == 0,
            "original pending/selection/output owners released");
        internal async Task DisposeObservedAsync(List<Exception> failures)
        {
            Output.Release.TrySetResult();
            var cancel = Capture(() => Cancel.Cancel());
            if (cancel is not null) AddStage(failures, "fixture cancellation", cancel);
            await AttemptStageAsync(failures, "Connection.Dispose", () => Connection.DisposeAsync().AsTask());
            // If Connection.Dispose threw before it reached Session disposal, still attempt the
            // exact Session owner. Repeated fault observation stays separately attributed; no
            // exception-reference filtering or expected-output aggregate forgiveness.
            await AttemptStageAsync(failures, "Session.Dispose", () => Session.DisposeAsync().AsTask());
            await AttemptStageAsync(failures, "Client.Dispose", () => Client.DisposeAsync().AsTask());
            await AttemptStageAsync(failures, "input pipe writer", () => _input.Writer.CompleteAsync().AsTask());
            await AttemptStageAsync(failures, "output pipe reader", () => _output.Reader.CompleteAsync().AsTask());
            var dispose = Capture(() => Cancel.Dispose());
            if (dispose is not null) AddStage(failures, "fixture cancellation source Dispose", dispose);
        }

        public async ValueTask DisposeAsync()
        {
            Output.Release.TrySetResult(); Cancel.Cancel();
            await Connection.DisposeAsync();
            if (!StopResultObserved) await Client.DisposeAsync(); // Negative Stop case already joined its exact expected failure.
            await _input.Writer.CompleteAsync(); await _output.Reader.CompleteAsync(); Cancel.Dispose();
        }
    }
    private sealed class HeldOutput(System.IO.Pipelines.PipeWriter inner, bool hold) : System.IO.Pipelines.PipeWriter
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? Failure;
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int hint = 0) => inner.GetMemory(hint);
        public override Span<byte> GetSpan(int hint = 0) => inner.GetSpan(hint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? error = null) => inner.Complete(error);
        public override ValueTask CompleteAsync(Exception? error = null) => inner.CompleteAsync(error);
        public override async ValueTask<System.IO.Pipelines.FlushResult> FlushAsync(CancellationToken token = default)
        {
            if (hold) { Entered.TrySetResult(); await Release.Task; }
            if (Failure is { } error) throw error;
            return await inner.FlushAsync(token);
        }
    }
}
