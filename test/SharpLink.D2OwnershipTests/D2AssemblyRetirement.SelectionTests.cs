using SharpLink.Client;
using SharpLink.RetirementEntryPlugin;

namespace SharpLink.UnitTests.Client;

public sealed partial class D2AssemblyRetirementTests
{
    [Test]
    public Task D201RejectedChildHasNoFabricatedPhysicalReceipt()
        => Run(async f =>
        {
            var before = f.SnapshotIdentity;
            var replacement = f.NewPlugin();
            var marker = new InvalidOperationException("d2 final validation rejection");
            f.ChildHook = stage => { if (stage == "BeforeFinalValidation") throw marker; };
            var waiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly)));
            var result = await f.Result(waiter);
            Ensure(!result.Succeeded && result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                result.Error.Message == "Assembly replacement failed transactionally: InvalidOperationException: d2 final validation rejection",
                "original child structured rejection, not an invented physical failure");
            var owner = f.LastOwner; await f.JoinOwner(owner);
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            Ensure(receipt.Disposition == ChildAssemblyDisposition.RejectedWithoutSelection && receipt.IsFinalized &&
                receipt.Control is null && receipt.Physical is null && receipt.Observation is null,
                "no selection has no physical receipt or release certificate");
            Ensure(ReferenceEquals(before, f.SnapshotIdentity) && f.RoutesTo(f.OldAssembly) &&
                !f.RoutesTo(replacement.Assembly) && f.Module.State == SharpLinkDynamicModuleState.Running,
                "prebuilt candidate never replaces original route authority");
            Ensure(Read<TaskCompletionSource<bool>>(owner, "PublicationDone").Task.IsCompletedSuccessfully &&
                Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded,
                "actual no-selection rollback finalizes and refunds its own inventory");
        });

    [Test]
    public Task D203PartialStartFailureAbortsAndJoinsStartedRoots()
        => Run(async f =>
        {
            var replacement = f.NewPlugin();
            var marker = new InvalidOperationException("d2 rooted physical construction failure");
            Task? driver = null; Task? physical = null; Task? observation = null;
            f.ChildHook = stage =>
            {
                if (stage != "PhysicalRooted") return;
                var control = f.Module.RetirementControl!;
                driver = f.Own(control.CreatedDriver!.Task);
                physical = f.Own(f.Module.RetirementOperation!);
                Ensure(f.Module.RetirementObservation is null, "observer not yet constructed at actual failure boundary");
                throw marker;
            };
            f.ParentHook = (stage, _) =>
            {
                if (stage == "BeforeDispositionFinalize" && f.Module.RetirementObservation is { } actual)
                    observation = f.Own(actual);
            };
            var waiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly)));
            var result = await f.Result(waiter);
            Ensure(!result.Succeeded && result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                result.Error.Message == "Assembly replacement failed transactionally: InvalidOperationException: d2 rooted physical construction failure",
                "source construction error remains the exact structured rejection");
            var owner = f.LastOwner; await f.JoinOwner(owner);
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            var selected = receipt.Control ?? throw new InvalidOperationException("Actual selected control is missing.");
            Ensure(receipt.Disposition == ChildAssemblyDisposition.SelectedAborted && receipt.AbortCertified &&
                ReferenceEquals(driver, selected.CreatedDriver!.Task) &&
                ReferenceEquals(physical, receipt.Physical) && ReferenceEquals(observation, receipt.Observation),
                "actual pre-start roots delivered and joined, never replaced by completed sentinels");
            Ensure(driver is { IsCompletedSuccessfully: true } && physical is { IsCompletedSuccessfully: true } &&
                observation is { IsCompletedSuccessfully: true } && selected.StartDecision.Result == false &&
                selected.ReservationLeaseReturned && !selected.CapacityRefunded &&
                f.Module.State == SharpLinkDynamicModuleState.Running && f.RoutesTo(f.OldAssembly),
                "Abort returned borrowed stage lease; running module capacity is not falsely refunded");
        });

    [Test]
    public Task D205ConcurrentUnregisterSharesExactWinnerAndBudget()
        => Run(async f =>
        {
            f.HoldCall(); var callback = f.NewGate(); f.OnCancellation(callback.Wait);
            using var caller = new CancellationTokenSource();
            var first = await f.Result(f.Invoke(() => f.Unregister(TimeSpan.Zero, caller.Token)));
            await f.Within(callback.Entered.Task);
            var owner = f.LastOwner;
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            var physical = receipt.Physical ?? throw new InvalidOperationException("Actual selected physical task is missing.");
            var second = await f.Result(f.Invoke(() => f.Unregister(TimeSpan.FromDays(1))));
            Ensure(f.OwnerCount == 1 && ReferenceEquals(owner, f.LastOwner) &&
                ReferenceEquals(physical, receipt.Physical) && physical is { IsCompleted: false },
                "one original selected outer/child owner, not two caller wrappers");
            caller.Cancel(); await f.ExpectCallerCancellation(first, caller.Token);
            Ensure(!Read<Task>(owner, "Worker").IsCompleted && !physical!.IsCompleted,
                "canceled caller does not abort actual source ownership");
            callback.Release.Set();
            Ensure(!(await f.Result(second)).ReferencesReleased && !physical.IsCompleted,
                "winner zero-budget grace does not wait a later caller's one-day budget");
            f.ReleaseCall(); await f.JoinOwner(owner);
            Ensure(receipt.HasPhysicalExit && receipt.ModuleReleased && receipt.AccountingRefunded,
                "all three committed child facts are actual after the original join");
        });

    [Test]
    public Task D207CallbackBeforeClaimCannotHoldPublicationGate()
        => Run(async f =>
        {
            var replacement = f.NewPlugin(blocked: true);
            var claims = 0;
            f.ParentHook = (stage, _) => { if (stage == "BeforeWindowClaim") Interlocked.Increment(ref claims); };
            var invocation = f.Invoke(() => f.Replace(replacement.Assembly));
            await f.Within(replacement.Entered.Task);
            Ensure(!invocation.IsCompleted && f.OwnerCount == 0, "actual loader is held before any selected outer owner");
            var stop = f.Stop();
            Ensure(Read<bool>(f.Multi, "_assemblySealed") && !stop.IsCompleted && claims == 0,
                "Stop acquired the publication gate and sealed; it joins the original live preparation");
            replacement.Release.TrySetResult();
            var waiter = await f.Result(invocation);
            var result = await f.Result(waiter);
            Ensure(!result.Succeeded && result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState &&
                replacement.Count(nameof(EntryState.ManifestCount)) == 1 && claims == 0 && f.OwnerCount == 0,
                "postcallback identity revalidation rejects without callback replay or stale window");
            await f.Within(stop);
        });

    [Test]
    public async Task D227TrackFailureAbortsBeforeChildInvocation()
    {
        foreach (var phase in new[] { "BeforeTracking", "AfterTracking" })
            await Run(async f =>
            {
                var marker = new InvalidOperationException("d2 " + phase);
                var childStages = 0; var childInvocations = 0;
                f.ChildHook = _ => Interlocked.Increment(ref childStages);
                f.ParentHook = (stage, _) =>
                {
                    if (stage == "BeforeChildInvocation") Interlocked.Increment(ref childInvocations);
                    if (stage == phase) throw marker;
                };
                var waiter = await f.Result(f.Invoke(() => f.Unregister()));
                await f.ExpectFault(waiter, marker);
                var owner = f.LastOwner; await f.JoinOwner(owner);
                var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
                Ensure(Read<TaskCompletionSource<bool>>(owner, "Start").Task.IsCompletedSuccessfully &&
                    !Read<TaskCompletionSource<bool>>(owner, "Start").Task.Result && childStages == 0 && childInvocations == 0 &&
                    receipt.Control is null && receipt.Physical is null && receipt.Observation is null,
                    "real installed worker receives Abort; no child invocation or invented receipt");
                Ensure(Read<Task>(owner, "Worker").IsCompletedSuccessfully &&
                    Read<Task>(owner, "Observer").IsCompletedSuccessfully &&
                    Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded && f.RoutesTo(f.OldAssembly),
                    "original worker and known receipt joined before actual capacity refund");
            });
    }

    [Test]
    public async Task D226AbortAndCommitFinalizeBeforeActualActivation()
    {
        foreach (var commit in new[] { false, true })
            await Run(async f =>
            {
                var replacement = f.NewPlugin();
                var construction = new InvalidOperationException("d2 partial child construction");
                var handoff = new InvalidOperationException("d2 independent handoff stage");
                var entered = f.NewGate(); var observedOutside = false; var startPending = false;
                RetirementControl? selected = null; Task? physical = null; Task? observation = null;
                if (!commit) f.ChildHook = stage => { if (stage == "ObserverRooted") throw construction; };
                f.ParentHook = (stage, owner) =>
                {
                    if (stage != "BeforeDispositionFinalize") return;
                    var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
                    selected = receipt.Control; physical = receipt.Physical; observation = receipt.Observation;
                    observedOutside = !Read<Lock>(f.Registry, "_gate").IsHeldByCurrentThread &&
                        !Read<Lock>(f.Multi, "_gate").IsHeldByCurrentThread;
                    startPending = selected is not null && !selected.StartDecision.IsCompleted &&
                        physical is { IsCompleted: false } && observation is { IsCompleted: false };
                    entered.Wait();
                    throw handoff;
                };
                var waiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly)));
                await f.Within(entered.Entered.Task);
                Ensure(observedOutside && startPending && selected is not null && selected.IsCommitted == commit,
                    "actual three-root disposition handoff runs outside both gates before either Abort or Commit activation");
                entered.Release.Set();
                await f.ExpectFault(waiter, handoff);
                var owner = f.LastOwner; await f.JoinOwner(owner);
                var final = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
                Ensure(ReferenceEquals(physical, final.Physical) && ReferenceEquals(observation, final.Observation) &&
                    selected!.StartDecision.IsCompletedSuccessfully && selected.StartDecision.Result == commit &&
                    Read<TaskCompletionSource<bool>>(owner, "PublicationDone").Task.IsCompletedSuccessfully &&
                    ReadOptional<object>(f.Multi, "_assemblyWindow") is null,
                    "handoff error cannot skip real source activation, exact joins or publication completion");
                Ensure(commit ? final.HasPhysicalExit && final.ModuleReleased && final.AccountingRefunded &&
                    f.RoutesTo(replacement.Assembly) && !f.RoutesTo(f.OldAssembly) :
                    final.AbortCertified && f.RoutesTo(f.OldAssembly) && !f.RoutesTo(replacement.Assembly),
                    "committed mandatory swap and aborted unchanged route retain their distinct certificates");
            });
    }

    [Test]
    public Task D204PreclaimCapacityFailureDoesNotPublishOrLeakRetains()
        => Run(async f =>
        {
            var replacement = f.NewPlugin(); var before = f.SnapshotIdentity;
            var marker = ColdMetadataException.Capacity(); var claims = 0; ulong? warmBytes = null;
            f.ParentHook = (stage, _) =>
            {
                if (stage != "BeforeWindowClaim") return;
                Interlocked.Increment(ref claims); throw marker;
            };
            for (var pass = 0; pass < 2; pass++)
            {
                var waiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly)));
                var rejected = await f.Result(waiter);
                var owner = f.LastOwner; await f.JoinOwner(owner);
                var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
                Ensure(!rejected.Succeeded && rejected.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded &&
                    receipt.Control is null && receipt.Physical is null && receipt.Observation is null &&
                    receipt.Disposition == ChildAssemblyDisposition.RejectedWithoutSelection &&
                    !Read<bool>(owner, "_published") && ReadOptional<object>(f.Multi, "_assemblyWindow") is null &&
                    ReferenceEquals(before, f.SnapshotIdentity) && f.RoutesTo(f.OldAssembly),
                    "preclaim injected capacity error cannot select child or publish a new authority");
                Ensure(ReadOptional<object>(owner, "_candidateSnapshot") is null &&
                    ReadOptional<object>(owner, "_candidateRegistration") is null &&
                    ReadOptional<object>(owner, "_storage") is null &&
                    ReadOptional<object>(owner, "_incomingLease") is null &&
                    Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded,
                    "each successful prebuild retain was actually cleaned and only elected inventory refunded");
                if (warmBytes is { } previous) Ensure(f.Authority.Bytes == previous,
                    "repeated same-capacity rejection adds no hidden owner/metadata history charge");
                warmBytes = f.Authority.Bytes;
            }
            Ensure(claims == 2 && f.Module.State == SharpLinkDynamicModuleState.Running,
                "two original failed preclaim executions leave original module running");
        });

    [Test]
    public Task D206CertifiedAbortReelectsWithOriginalBudget()
        => Run(async f =>
        {
            var replacement = f.NewPlugin(); var before = f.SnapshotIdentity;
            var construction = new InvalidOperationException("d2 predecessor child Abort");
            var directFinalize = f.NewGate(); var oldRefund = f.NewGate(); var successorCall = f.NewGate();
            var parentFinalized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var direct = new ChildAssemblySelectionMailbox(static () => { }, _ => directFinalize.Wait());
            f.ChildHook = stage => { if (stage == "ObserverRooted") throw construction; };
            var directInvocation = f.Invoke(() =>
            {
                var capability = (ISharpLinkPreparedAssemblyClient)f.Child;
                var budget = capability.CaptureRetirementBudget(TimeSpan.Zero);
                using var preparation = Read<SharpLinkRuntimeContext>(f.Child, "_runtimeContext").Domain.BeginPreparation();
                var loaded = SharpLinkAssemblyManifestLoader.TryLoad(replacement.Assembly, preparation, out var manifest);
                Ensure(loaded.Succeeded, "actual current candidate was prepared in the child domain");
                return f.Own(capability.ReplacePreparedAssemblyAsync(f.OldAssembly, manifest!, budget, direct).AsTask());
            });
            await f.Within(directFinalize.Entered.Task);
            var childControl = direct.Control!;
            var childPhysical = f.Own(direct.Physical!); var childObservation = f.Own(direct.Observation!);
            f.Own(childControl.CreatedDriver!.Task);
            Ensure(direct.Disposition == ChildAssemblyDisposition.SelectedAborted && !childControl.StartDecision.IsCompleted,
                "real direct child stage is selected Abort, outside the gate, not yet activated");
            f.ChildHook = null;
            object? first = null;
            f.ParentHook = (stage, owner) =>
            {
                if (stage == "BeforeTracking" && first is null) first = owner;
                if (ReferenceEquals(first, owner) && stage == "BeforeDispositionFinalize") parentFinalized.TrySetResult();
                if (ReferenceEquals(first, owner) && stage == "BeforeFinalRefund") oldRefund.Wait();
                if (!ReferenceEquals(first, owner) && stage == "BeforeChildInvocation") successorCall.Wait();
            };
            var original = await f.Result(f.Invoke(() => f.Unregister()));
            await f.Within(parentFinalized.Task);
            var firstOwner = first ?? throw new InvalidOperationException("Original selected owner was not observed.");
            var firstReceipt = Read<ChildAssemblySelectionMailbox>(firstOwner, "Mailbox");
            var originalPublic = Read<object>(firstOwner, "Public");
            var repeated = await f.Result(f.Invoke(() => f.Unregister(TimeSpan.FromDays(1))));
            Ensure(f.OwnerCount == 1 && !original.IsCompleted && !repeated.IsCompleted &&
                firstReceipt.Disposition == ChildAssemblyDisposition.JoinedUncommittedStage &&
                ReferenceEquals(firstReceipt.Control, childControl) && ReferenceEquals(firstReceipt.Physical, childPhysical) &&
                ReferenceEquals(firstReceipt.Observation, childObservation) && ReferenceEquals(before, f.SnapshotIdentity),
                "both parent waiters join one exact original child Abort without publishing or recapturing a winner");
            directFinalize.Release.Set();
            var directWaiter = await f.Result(directInvocation);
            Ensure(!(await f.Result(directWaiter)).Succeeded, "original child still reports its actual structured rejection");
            await f.Within(oldRefund.Entered.Task);
            var oldCapacity = Read<CoordinatorAssemblyCapacity>(firstOwner, "Capacity");
            var oldToken = Read<ColdMetadataReservation>(oldCapacity, "_token");
            Ensure(firstReceipt.AbortCertified && !oldCapacity.Refunded && !oldToken.IsRefunded &&
                f.OwnerCount == 1 && !original.IsCompleted && !repeated.IsCompleted,
                "actual successful Abort join alone cannot authorize a new96 election before old refund");
            oldRefund.Release.Set(); await f.Within(successorCall.Entered.Task);
            var next = f.LastOwner; var nextCapacity = Read<CoordinatorAssemblyCapacity>(next, "Capacity");
            var nextBudget = Read<ClientRetirementBudget>(next, "Budget").GetFor(f.Registry);
            var oldBudget = Read<ClientRetirementBudget>(firstOwner, "Budget").GetFor(f.Registry);
            Ensure(f.OwnerCount == 2 && !ReferenceEquals(firstOwner, next) && oldCapacity.Refunded && oldToken.IsRefunded &&
                !ReferenceEquals(nextCapacity, oldCapacity) && !ReferenceEquals(Read<object>(nextCapacity, "_token"), oldToken) &&
                ReferenceEquals(Read<object>(next, "Public"), originalPublic) &&
                !ReferenceEquals(Read<object>(next, "Start"), Read<object>(firstOwner, "Start")) &&
                !ReferenceEquals(Read<object>(next, "PublicationDone"), Read<object>(firstOwner, "PublicationDone")) &&
                !ReferenceEquals(Read<object>(next, "Failures"), Read<object>(firstOwner, "Failures")) &&
                Read<Task>(firstOwner, "Observer").IsCompletedSuccessfully && ReadOptional<Task>(next, "Predecessor") is null &&
                nextBudget.TimestampOrigin == oldBudget.TimestampOrigin && nextBudget.Duration == oldBudget.Duration &&
                ReferenceEquals(nextBudget.Provider, oldBudget.Provider) && !original.IsCompleted && !repeated.IsCompleted,
                "one pending Public moves to fresh stage only after original observer join and actual old96 refund");
            successorCall.Release.Set();
            Ensure((await f.Result(original)).ReferencesReleased && (await f.Result(repeated)).ReferencesReleased,
                "one successor completes both waiters without per-waiter re-election");
            await f.JoinOwner(firstOwner); await f.JoinOwner(next);
            Ensure(f.OwnerCount == 2 && nextCapacity.Refunded && !f.RoutesTo(f.OldAssembly),
                "exactly one fresh selected child stage physically completes and refunds");
        });

    [Test]
    public Task D225NoSelectionFinalGateRejectionFinalizesBeforeRollback()
        => Run(async f =>
        {
            f.HoldCall(); var replacement = f.NewPlugin(); var rollback = f.NewGate();
            var callbackOutside = false; var scopeCalls = 0; Task<SharpLinkAssemblyUnregisterResult>? direct = null;
            replacement.SetScopeDisposing(() =>
            {
                Interlocked.Increment(ref scopeCalls);
                callbackOutside = !Read<Lock>(f.Registry, "_gate").IsHeldByCurrentThread &&
                    !Read<Lock>(f.Multi, "_gate").IsHeldByCurrentThread;
                rollback.Wait();
            });
            f.ChildHook = stage =>
            {
                if (stage != "BeforeFinalValidation") return;
                // Real independent child mutation, not a hook-thrown substitute
                // for the source's final-gate identity/admission rejection.
                direct = f.Own(f.Child.UnregisterAssemblyAsync(f.OldAssembly, TimeSpan.Zero).AsTask());
                f.Own(f.Module.RetirementOperation!); f.Own(f.Module.RetirementObservation!);
                f.Own(f.Module.RetirementControl!.CreatedDriver!.Task);
            };
            var invocation = f.Invoke(() => f.Replace(replacement.Assembly));
            var waiter = await f.Result(invocation);
            await f.Within(rollback.Entered.Task);
            var owner = f.LastOwner; var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            var worker = Read<Task>(owner, "Worker");
            Ensure(callbackOutside && !worker.IsCompleted && !waiter.IsCompleted && scopeCalls == 1 &&
                receipt.Disposition == ChildAssemblyDisposition.RejectedWithoutSelection &&
                receipt.Control is null && receipt.Physical is null && receipt.Observation is null &&
                Read<TaskCompletionSource<bool>>(owner, "PublicationDone").Task.IsCompletedSuccessfully &&
                ReadOptional<object>(f.Multi, "_assemblyWindow") is null,
                "actual rejected final gate hands off and releases window before real rollback Dispose callback can block");
            var independent = typeof(SharpLink.MultiClusterTest.Contracts.IOrdersContract).Assembly;
            var registered = await f.Result(f.Invoke(() => f.Multi.RegisterAssembly(f.OtherKey, independent)));
            Ensure(registered.Succeeded && f.OtherChild.IsDynamicAssemblyRegistered(independent) &&
                f.RoutesTo(independent) && f.RoutesTo(f.OldAssembly) && !worker.IsCompleted && !waiter.IsCompleted,
                "independent real cold publisher completes while the original rollback callback remains held");
            rollback.Release.Set();
            var rejected = await f.Result(waiter);
            Ensure(!rejected.Succeeded && rejected.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState &&
                rejected.Error.Message == "The old Assembly object changed while the replacement candidate was being prepared." &&
                replacement.Count(nameof(SharpLink.RetirementEntryPlugin.EntryState.ScopeDisposeEntered)) == 1 &&
                replacement.Count(nameof(SharpLink.RetirementEntryPlugin.EntryState.ScopeDisposeExited)) == 1,
                "actual final-gate rejection and one original rollback Dispose retain their precise result");
            await f.JoinOwner(owner); f.ReleaseCall();
            Ensure(direct is not null, "the actual rejecting child operation was retained");
            await f.Within(direct!); await f.Within(f.Module.RetirementOperation!); await f.Within(f.Module.RetirementObservation!);
            Ensure(f.RoutesTo(independent) && !f.RoutesTo(replacement.Assembly),
                "late rollback cannot replay its obsolete source snapshot over the independent publication");
        }, secondary: true);

    [Test]
    public Task D202SelectedChildThrowRetainsExactOriginalTasks()
        => Run(async f =>
        {
            f.HoldCall(); var callback = f.NewGate(); f.OnCancellation(callback.Wait);
            var marker = new InvalidOperationException("d2 real decorator synchronous invocation occurrence");
            var decorator = f.Decorator!; decorator.ThrowAfterSelection = marker;
            var waiter = await f.Result(f.Invoke(() => f.Unregister()));
            await f.Within(decorator.Selected.Task); await f.Within(callback.Entered.Task);
            var owner = f.LastOwner; var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            var physical = f.Own(receipt.Physical!); var observation = f.Own(receipt.Observation!);
            var driver = f.Own(receipt.Control!.CreatedDriver!.Task);
            var original = decorator.OriginalReturned ?? throw new InvalidOperationException("Real child returned task missing.");
            Ensure(receipt.Disposition == ChildAssemblyDisposition.SelectedCommitted &&
                ReferenceEquals(receipt.InvocationFailure, marker) && receipt.ReturnedFailureIsSourceProjection &&
                ReferenceEquals(physical, f.Module.RetirementOperation) && ReferenceEquals(observation, f.Module.RetirementObservation) &&
                !physical.IsCompleted && !observation.IsCompleted && !driver.IsCompleted && !waiter.IsCompleted &&
                !Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded,
                "independent synchronous decorator throw cannot discard, replace or prematurely certify actual child roots");
            callback.Release.Set(); f.ReleaseCall();
            await f.ExpectFault(waiter, marker); await f.JoinOwner(owner);
            await f.Within(original); await f.Within(physical); await f.Within(observation); await f.Within(driver);
            Ensure(receipt.HasPhysicalExit && receipt.ModuleReleased && receipt.AccountingRefunded &&
                Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded,
                "only actual source join and refund permit release while independent invocation failure remains visible once");
        }, decorate: true);
}
