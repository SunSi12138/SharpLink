using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class D2AssemblyRetirementTests
{
    [Test]
    public Task D223RepeatedStopAndCanceledWaitersRetainOriginalOwners()
        => Run(async f =>
        {
            f.HoldCall(); var callback = f.NewGate(); f.OnCancellation(callback.Wait);
            using var caller = new CancellationTokenSource();
            var waiter = await f.Result(f.Invoke(() => f.Unregister(token: caller.Token)));
            await f.Within(callback.Entered.Task);
            var owner = f.LastOwner; var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            var physical = receipt.Physical; var observation = receipt.Observation;
            caller.Cancel(); await f.ExpectCallerCancellation(waiter, caller.Token);
            using var stopCaller = new CancellationTokenSource();
            var original = f.Stop();
            var repeated = f.Own(f.Multi.StopAsync().AsTask());
            var abandoned = f.Own(f.Multi.StopAsync(stopCaller.Token).AsTask());
            Ensure(ReferenceEquals(original, repeated) && ReferenceEquals(original, Read<Task>(f.Multi, "_stopTask")) &&
                !original.IsCompleted && receipt.Physical is { IsCompleted: false },
                "repeated Stop shares one exact original while physical callback remains held");
            stopCaller.Cancel(); await f.ExpectCallerCancellation(abandoned, stopCaller.Token);
            Ensure(!original.IsCompleted && ReferenceEquals(physical, receipt.Physical) &&
                ReferenceEquals(observation, receipt.Observation) && !receipt.HasPhysicalExit &&
                !Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded,
                "two caller wrapper cancellations cannot counterfeit durable physical/accounting completion");
            callback.Release.Set(); f.ReleaseCall();
            await f.Within(original); await f.JoinOwner(owner);
            Ensure(receipt.HasPhysicalExit && receipt.ModuleReleased && receipt.AccountingRefunded &&
                Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded,
                "one actual child/outer Stop joins and certifies original resources");
        });

    [Test]
    public Task D219SupervisorTerminalCannotReleaseDurableOwner()
        => Run(async f =>
        {
            var refund = f.NewGate();
            f.ParentHook = (stage, _) => { if (stage == "BeforeFinalRefund") refund.Wait(); };
            var waiter = await f.Result(f.Invoke(() => f.Unregister()));
            await f.Within(refund.Entered.Task);
            var owner = f.LastOwner; var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            var supervision = Read<FrameworkTaskRegistration>(owner, "Supervision");
            Ensure(Read<Task>(owner, "Worker").IsCompletedSuccessfully && supervision.RetainedOwnerRoot is null &&
                !Read<Task>(owner, "Observer").IsCompleted && !waiter.IsCompleted &&
                !Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded &&
                receipt.HasPhysicalExit && receipt.ModuleReleased && receipt.AccountingRefunded,
                "actual supervisor completion and child certificates do not stand in for outer accounting");
            lock (Read<Lock>(f.Multi, "_gate"))
                Ensure(ReferenceEquals(ReadOptional<object>(f.Multi, "_assemblyOwners"), owner) &&
                    Read<int>(f.Multi, "_assemblyOwnerCount") == 1,
                    "exact typed durable root survives supervisor terminal until final observer refund");
            refund.Release.Set();
            Ensure((await f.Result(waiter)).ReferencesReleased, "one original operation completes after actual final refund");
            await f.JoinOwner(owner);
            Ensure(Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded &&
                ReadOptional<object>(f.Multi, "_assemblyOwners") is null,
                "certified observer completion, not supervisor terminal, releases the durable root");
        });

    [Test]
    public Task D216OuterCleanupFailureRetainsItsOwnTypedOwner()
        => Run(async f =>
        {
            var marker = new InvalidOperationException("d2 pre-effect outer refund failure");
            var attempts = 0;
            f.ParentHook = (stage, _) =>
            {
                if (stage != "BeforeFinalRefund") return;
                Interlocked.Increment(ref attempts); throw marker;
            };
            var waiter = await f.Result(f.Invoke(() => f.Unregister()));
            await f.ExpectFault(waiter, marker);
            var owner = f.LastOwner; await f.JoinOwner(owner);
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            var capacity = Read<CoordinatorAssemblyCapacity>(owner, "Capacity");
            var token = Read<ColdMetadataReservation>(capacity, "_token");
            Ensure(receipt.HasPhysicalExit && receipt.ModuleReleased && receipt.AccountingRefunded &&
                !capacity.Refunded && !token.IsRefunded && Read<ulong>(token, "_bytes") == 12544 &&
                !Read<bool>(owner, "Certified"), "child release does not certify failed independent outer refund stage");
            var originalStop = f.Stop();
            var failure = await f.Failure(originalStop) ?? throw new InvalidOperationException("Expected typed failclosed Stop.");
            var occurrences = StopOccurrences(failure);
            Ensure(occurrences.Length == 2 && occurrences[0].Stage == "OuterStage" &&
                ReferenceEquals(occurrences[0].Owner, owner) && ReferenceEquals(occurrences[0].Original, Read<Task>(owner, "Observer")) &&
                occurrences[0].OuterStageIndex == 14 && ReferenceEquals(occurrences[0].Failure, marker) &&
                occurrences[1].Stage == "UncertifiedOwner" && ReferenceEquals(occurrences[1].Owner, owner) &&
                occurrences[1].Failure is null, "one independent refund occurrence and separate missing-certificate fact, no child-source replay");
            Ensure(attempts == 1 && !capacity.Refunded && !token.IsRefunded &&
                ReferenceEquals(ReadOptional<object>(f.Multi, "_assemblyOwners"), owner) &&
                Read<ulong>(token, "_bytes") == 12544, "failed Stop retains exact typed owner/token and never guesses a retry/refund");
            f.AcceptInspectedRetainedStop(failure);
        });

    [Test]
    public Task D218SourceOccurrenceAndIndependentOccurrencesStayDistinct()
        => Run(async f =>
        {
            f.HoldCall(); var marker = new InvalidOperationException("same raw identity, three independently executed stages");
            var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbacks = 0; var handoffs = 0; var refunds = 0;
            f.OnCancellation(() => { Interlocked.Increment(ref callbacks); called.TrySetResult(); throw marker; });
            f.ParentHook = (stage, _) =>
            {
                if (stage == "BeforeDispositionFinalize") { Interlocked.Increment(ref handoffs); throw marker; }
                if (stage == "BeforeFinalRefund") { Interlocked.Increment(ref refunds); throw marker; }
            };
            var waiter = await f.Result(f.Invoke(() => f.Unregister()));
            await f.Within(called.Task); f.ReleaseCall();
            await f.ExpectOccurrences(waiter, marker, 3);
            var owner = f.LastOwner; await f.JoinOwner(owner);
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            await f.ExpectOccurrences(receipt.Physical!, marker, 1);
            Ensure(receipt.HasPhysicalExit && receipt.ModuleReleased && receipt.AccountingRefunded &&
                !Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded &&
                callbacks == 1 && handoffs == 1 && refunds == 1,
                "source failure is one execution; independent handoff/refund stages share raw identity without deduplication");
            var stop = f.Stop(); var failure = await f.Failure(stop) ?? throw new InvalidOperationException("Expected typed Stop source summary.");
            var occurrences = StopOccurrences(failure);
            var child = occurrences.Where(value => value.Stage == "ChildStop").ToArray();
            var outer = occurrences.Where(value => value.Stage == "OuterStage").ToArray();
            var absent = occurrences.Where(value => value.Stage == "UncertifiedOwner").ToArray();
            Ensure(occurrences.Length == 4 && child.Length == 1 && child[0].Failure is { } source && Count(source, marker) == 1 &&
                Only(source, marker) && ReferenceEquals(child[0].Original, Read<Task>(f.Child, "_stopTask")) &&
                outer.Length == 2 && outer.Select(value => value.OuterStageIndex).Order().SequenceEqual(new[] { 4, 14 }) &&
                outer.All(value => ReferenceEquals(value.Owner, owner) && ReferenceEquals(value.Failure, marker) &&
                    ReferenceEquals(value.Original, Read<Task>(owner, "Observer"))) &&
                absent.Length == 1 && ReferenceEquals(absent[0].Owner, owner) && absent[0].Failure is null,
                "childStop owns one original physical source; outer reports only its two independent occurrences and missing certificate");
            var childStop = f.Own(Read<Task>(f.Child, "_stopTask"));
            await f.ExpectOccurrences(childStop, marker, 1);
            f.AcceptInspectedRetainedStop(failure);
        });

    [Test]
    public async Task D214UnknownTimerExitDoesNotCertifyPhysicalRelease()
    {
        var marker = new InvalidOperationException("d2 actual timer DisposeAsync failed");
        var clock = new RetainedTimerClock(marker);
        await Run(async f =>
        {
            f.HoldCall();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.OnCancellation(() => cancelled.TrySetResult());
            var waiter = await f.Result(f.Invoke(() => f.Unregister(TimeSpan.FromSeconds(1))));
            await f.Within(clock.Armed.Task); clock.Advance(TimeSpan.FromSeconds(1));
            await f.Within(cancelled.Task); f.ReleaseCall();
            await f.ExpectOccurrences(waiter, marker, 1);
            var owner = f.LastOwner; await f.JoinOwner(owner);
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            await f.ExpectOccurrences(receipt.Physical!, marker, 1);
            var control = receipt.Control!; var driver = await f.Result(f.Own(control.Driver.Task));
            var childToken = Read<ColdMetadataReservation>(f.Module, "_retirementCapacity");
            var outer = Read<CoordinatorAssemblyCapacity>(owner, "Capacity");
            var childBytes = Read<ulong>(childToken, "_bytes");
            Ensure(driver.HasRetainedTimer && ReferenceEquals(Read<ITimer>(control.Driver, "_ownedTimer"), clock.Timer) &&
                !receipt.HasPhysicalExit && !receipt.ModuleReleased && !receipt.AccountingRefunded &&
                !control.ReservationLeaseReturned && !outer.Refunded && childBytes != 0 &&
                f.Child.IsDynamicAssemblyRegistered(f.OldAssembly),
                "faulted original tasks do not prove actual timer exit, module release or either refund");
            var stop = f.Stop(); var failure = await f.Failure(stop) ?? throw new InvalidOperationException("Expected retained timer Stop failure.");
            var occurrences = StopOccurrences(failure);
            Ensure(occurrences.Length == 2 && occurrences[0].Stage == "ChildStop" &&
                occurrences[0].Failure is { } source && Count(source, marker) == 1 && Only(source, marker) &&
                ReferenceEquals(occurrences[0].Original, Read<Task>(f.Child, "_stopTask")) &&
                occurrences[1].Stage == "UncertifiedOwner" && ReferenceEquals(occurrences[1].Owner, owner) &&
                occurrences[1].Failure is null, "one child source graph and missing-release fact, not repeated physical fault");
            await f.ExpectOccurrences(f.Own(Read<Task>(f.Child, "_stopTask")), marker, 1);
            Ensure(clock.DisposeCalls == 1 && Read<int>(Read<SharpLinkRuntimeContext>(f.Child, "_runtimeContext"), "_disposed") == 0 &&
                Read<ulong>(childToken, "_bytes") == childBytes && !outer.Refunded &&
                ReferenceEquals(ReadOptional<object>(f.Multi, "_assemblyOwners"), owner),
                "unknown exit retains real module/runtime/outer custody with no blind Dispose retry");
            // This control owns the known fake's backing timer. Joining it out of
            // band is harness containment only, never a production certificate.
            await f.Within(f.Own(clock.EndKnownFake().AsTask()));
            Ensure(!receipt.HasPhysicalExit && !receipt.ModuleReleased && !receipt.AccountingRefunded &&
                !outer.Refunded && clock.DisposeCalls == 1, "harness timer join cannot forge production recovery");
            f.AcceptInspectedRetainedStop(failure);
        }, provider: clock);
    }

    private sealed class RetainedTimerClock(Exception failure) : TimeProvider
    {
        private readonly SharpLink.UnitTests.Runtime.ManualTimeProvider _inner = new();
        private readonly Exception _failure = failure;
        internal readonly TaskCompletionSource Armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal OwnedTimer? Timer;
        internal int DisposeCalls;
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();
        internal void Advance(TimeSpan value) => _inner.Advance(value);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Ensure(state is RetirementCancellationDriver && Timer is null, "one actual retirement timer owner");
            return Timer = new OwnedTimer(this, _inner.CreateTimer(callback, state, dueTime, period));
        }
        internal ValueTask EndKnownFake() => Timer?.EndKnownFake() ?? ValueTask.CompletedTask;
        internal sealed class OwnedTimer(RetainedTimerClock clock, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan due, TimeSpan period)
            { var result = inner.Change(due, period); clock.Armed.TrySetResult(); return result; }
            public void Dispose() => inner.Dispose();
            public ValueTask DisposeAsync()
            { Interlocked.Increment(ref clock.DisposeCalls); return ValueTask.FromException(clock._failure); }
            internal ValueTask EndKnownFake() => inner.DisposeAsync();
        }
    }

    [Test]
    public Task D221FailedChildStopDoesNotReleaseItsMetadata()
        => Run(async f =>
        {
            f.HoldCall(); var marker = new InvalidOperationException("d2 one child's physical source");
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.OnCancellation(() => { cancelled.TrySetResult(); throw marker; });
            var slots = f.WithSnapshot(snapshot => (Failed: snapshot.Clusters[f.Key], Succeeded: snapshot.Clusters[f.OtherKey]));
            var failedMetadata = slots.Failed.MetadataOwner;
            Ensure(failedMetadata is not null && slots.Succeeded.MetadataOwner is not null,
                "both actual child slots initially own their prepared metadata");
            var waiter = await f.Result(f.Invoke(() => f.Unregister()));
            await f.Within(cancelled.Task); f.ReleaseCall();
            await f.ExpectOccurrences(waiter, marker, 1);
            var owner = f.LastOwner; await f.JoinOwner(owner);
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            await f.ExpectOccurrences(receipt.Physical!, marker, 1);
            Ensure(receipt.HasPhysicalExit && receipt.ModuleReleased && receipt.AccountingRefunded &&
                Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded,
                "exact module cleanup may finish although original source failure remains reportable");
            var stop = f.Stop(); var failure = await f.Failure(stop) ?? throw new InvalidOperationException("Expected failed-child Stop summary.");
            var occurrences = StopOccurrences(failure);
            Ensure(occurrences.Length == 1 && occurrences[0].Stage == "ChildStop" &&
                ReferenceEquals(occurrences[0].Owner, slots.Failed) && occurrences[0].Failure is { } raw &&
                Count(raw, marker) == 1 && Only(raw, marker) &&
                ReferenceEquals(occurrences[0].Original, Read<Task>(f.Child, "_stopTask")),
                "original failing child source is reported once without synthesizing outer stage failures");
            await f.ExpectOccurrences(f.Own(Read<Task>(f.Child, "_stopTask")), marker, 1);
            await f.Within(f.Own(Read<Task>(f.OtherChild, "_stopTask")));
            Ensure(ReferenceEquals(slots.Failed.MetadataOwner, failedMetadata) && slots.Succeeded.MetadataOwner is null &&
                ReadOptional<object>(f.Multi, "_metadataOwner") is not null &&
                f.WithSnapshot(snapshot => ReferenceEquals(snapshot.Clusters[f.Key], slots.Failed) &&
                    ReferenceEquals(snapshot.Clusters[f.OtherKey], slots.Succeeded)),
                "only independently successful child metadata releases; failed slot and coordinator roots remain exact");
            f.AcceptInspectedRetainedStop(failure);
        }, secondary: true);

    [Test]
    public Task D220StopSealsAndTraversesPrepaidOwnerInventory()
        => Run(async f =>
        {
            f.HoldCall(); var replacement = f.NewPlugin();
            var oldWaiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly)));
            var oldResult = await f.Result(oldWaiter); var old = f.LastOwner;
            Ensure(oldResult.Succeeded && !oldResult.ReferencesReleased, "first real generation remains deferred");
            SharpLinkDynamicModule current;
            lock (Read<Lock>(f.Registry, "_gate")) current = f.Registry.DynamicModules[replacement.Assembly];
            var currentCall = f.OwnResource(new D2CallOwner(current));
            var marker = new InvalidOperationException("one sealed owner's independent refund stage"); var postSealReserves = 0;
            f.ParentHook = (stage, owner) =>
            {
                if (stage == "BeforeOriginalDetach" && Read<bool>(f.Multi, "_assemblySealed")) Interlocked.Increment(ref postSealReserves);
                if (stage == "BeforeFinalRefund" && ReferenceEquals(owner, old)) throw marker;
            };
            var nextWaiter = await f.Result(f.Invoke(() => f.Own(f.Multi.UnregisterAssemblyAsync(f.Key,
                replacement.Assembly, TimeSpan.Zero).AsTask())));
            Ensure(!(await f.Result(nextWaiter)).ReferencesReleased, "second real generation is independently deferred");
            var next = f.LastOwner;
            var entries = Read<Array>(f.Multi, "_assemblyStopEntries"); var failures = Read<Array>(f.Multi, "_assemblyStopFailures");
            Ensure(f.OwnerCount == 2 && !ReferenceEquals(old, next), "two independently admitted exact generation owners");
            var stop = f.Stop();
            Ensure(Read<bool>(f.Multi, "_assemblySealed") && !stop.IsCompleted &&
                ReferenceEquals(entries, Read<Array>(f.Multi, "_assemblyStopEntries")) &&
                ReferenceEquals(failures, Read<Array>(f.Multi, "_assemblyStopFailures")),
                "Stop seals and uses existing fixed inventory while both generations are physically held");
            var denied = await f.Result(f.Own(f.Multi.UnregisterAssemblyAsync(f.Key, replacement.Assembly, TimeSpan.Zero).AsTask()));
            Ensure(!denied.ReferencesReleased && f.OwnerCount == 2, "sealed public request cannot reserve another D2 stage");
            f.ReleaseCall(); currentCall.Dispose();
            var error = await f.Failure(stop) ?? throw new InvalidOperationException("Expected one failed sealed owner.");
            await f.JoinOwner(old); await f.JoinOwner(next);
            var occurrences = StopOccurrences(error);
            Ensure(occurrences.Length == 2 && occurrences[0].Stage == "OuterStage" &&
                occurrences[0].OuterStageIndex == 14 && ReferenceEquals(occurrences[0].Owner, old) &&
                ReferenceEquals(occurrences[0].Failure, marker) && occurrences[1].Stage == "UncertifiedOwner" &&
                ReferenceEquals(occurrences[1].Owner, old) && occurrences[1].Failure is null,
                "typed sealed traversal preserves only actual independent failure and its missing certificate");
            var selectedEntries = entries.Cast<object>().Where(row => ReadOptional<object>(row, "Slot") is not null).ToArray();
            Ensure(selectedEntries.Length == 1 && ReferenceEquals(Read<Task>(selectedEntries[0], "Original"), Read<Task>(f.Child, "_stopTask")) &&
                postSealReserves == 0 && f.OwnerCount == 2 &&
                !Read<CoordinatorAssemblyCapacity>(old, "Capacity").Refunded &&
                Read<CoordinatorAssemblyCapacity>(next, "Capacity").Refunded,
                "each real child Stop is invoked once for both owners; successful unrelated owner refunds without freeing failed owner");
            f.AcceptInspectedRetainedStop(error);
        });

    private sealed class D2CallOwner : IDisposable
    {
        private readonly Lock _gate = new();
        private SharpLinkDynamicModuleLease _lease;
        internal D2CallOwner(SharpLinkDynamicModule module)
        { Ensure(module.TryAcquire(false, out _lease), "real second-generation call acquired"); }
        public void Dispose()
        {
            lock (_gate)
            {
                if (!_lease.IsAcquired) return;
                var original = _lease; _lease = default; original.Dispose();
            }
        }
    }

    [Test]
    public Task D222CapacityIncludesRetainedAndCandidateOwners()
        => Run(async f =>
        {
            f.HoldCall(); var replacement = f.NewPlugin(); var rejectedCandidate = f.NewPlugin();
            var waiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly)));
            Ensure((await f.Result(waiter)).Succeeded, "real candidate published while original generation is held");
            var owner = f.LastOwner; var marker = new InvalidOperationException("d2 retained failed inventory");
            f.ParentHook = (stage, selected) =>
            { if (stage == "BeforeFinalRefund" && ReferenceEquals(selected, owner)) throw marker; };
            f.ReleaseCall(); await f.JoinOwner(owner);
            var retained = Read<CoordinatorAssemblyCapacity>(owner, "Capacity");
            var current = Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Single();
            var currentCapacity = current.Capacity!;
            Ensure(!retained.Refunded && currentCapacity.CanClaim && !ReferenceEquals(retained, currentCapacity) &&
                Read<int>(f.Multi, "_assemblyInventoryCount") == 2 &&
                Read<ulong>(Read<ColdMetadataReservation>(retained, "_token"), "_bytes") == 12544 &&
                Read<ulong>(Read<ColdMetadataReservation>(currentCapacity, "_token"), "_bytes") == 12544,
                "retained failed old owner and independently admitted replacement each consume real96 units");
            var blocker = f.OwnResource(Read<ColdMetadataPreparation>(f.Multi, "_metadataPreparation").ForkStorage());
            var available = ColdMetadataAuthority.AuthorityByteLimit - f.Authority.Bytes;
            var slots = checked((int)((available - ColdMetadataAuthority.HeaderCharge) / ColdMetadataAuthority.SlotCharge));
            blocker.ReserveCollection(slots); // Logical authority only: no giant collection allocation.
            var full = f.Authority.Bytes; var snapshot = f.SnapshotIdentity; var childCalls = 0;
            f.ParentHook = (stage, _) => { if (stage == "BeforeChildInvocation") Interlocked.Increment(ref childCalls); };
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var invocation = f.Invoke(() => f.Own(f.Multi.ReplaceAssemblyAsync(f.Key, replacement.Assembly,
                    rejectedCandidate.Assembly, TimeSpan.Zero).AsTask()));
                var error = await f.Failure(invocation);
                Ensure(error is ColdMetadataException { Code: SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded, InnerException: null },
                    "actual authority admission rejects before starting candidate preparation/work");
                await f.ExpectGraph(invocation, error!);
                Ensure(f.Authority.Bytes == full && f.OwnerCount == 1 && childCalls == 0 &&
                    Read<int>(f.Multi, "_assemblyInventoryCount") == 2 && ReferenceEquals(snapshot, f.SnapshotIdentity) &&
                    rejectedCandidate.Count(nameof(SharpLink.RetirementEntryPlugin.EntryState.ManifestCount)) == 0,
                    "repeated rejected admission adds no uncharged stage, hidden work or callback history");
            }
            blocker.Dispose();
            var stop = f.Stop(); var failure = await f.Failure(stop) ?? throw new InvalidOperationException("Expected retained failed owner.");
            var occurrences = StopOccurrences(failure);
            Ensure(occurrences.Length == 2 && occurrences[0].Stage == "OuterStage" &&
                occurrences[0].OuterStageIndex == 14 && ReferenceEquals(occurrences[0].Owner, owner) &&
                ReferenceEquals(occurrences[0].Failure, marker) && occurrences[1].Stage == "UncertifiedOwner" &&
                ReferenceEquals(occurrences[1].Owner, owner) && occurrences[1].Failure is null &&
                !retained.Refunded && Read<int>(f.Multi, "_assemblyInventoryCount") == 2,
                "releasing test pressure does not refund failed inventory or discard current metadata on failed Stop");
            f.AcceptInspectedRetainedStop(failure);
        });

    [Test]
    public Task D215ModuleReleaseDoesNotCertifyFailedChildRefund()
        => Run(async f =>
        {
            var marker = new InvalidOperationException("d2 actual last child capacity release");
            var raw = f.Module.RetirementCapacity;
            raw.FinalRefundFailureForDiagnostics = marker;
            var waiter = await f.Result(f.Invoke(() => f.Unregister()));
            await f.ExpectFault(waiter, marker);
            var owner = f.LastOwner; await f.JoinOwner(owner);
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            var observed = await f.Result(receipt.Observation!);
            await f.Within(receipt.Physical!);
            Ensure(receipt.HasPhysicalExit && receipt.ModuleReleased && !receipt.AccountingRefunded &&
                !receipt.Control!.ReservationLeaseReturned && ReferenceEquals(observed.Failure, marker) &&
                ReferenceEquals(Read<Exception>(owner, "_observerFailure"), marker) &&
                raw.IsDetached && raw.OriginalReleaseSucceeded && !raw.IsRefunded &&
                Read<int>(raw, "_references") == 0 && Read<ulong>(raw, "_bytes") == 5248 &&
                !Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded,
                "actual final-reference Dispose entered and failed before authority refund; Released module is not accounting proof");
            var stop = f.Stop(); var error = await f.Failure(stop) ?? throw new InvalidOperationException("Expected child refund Stop failure.");
            var occurrences = StopOccurrences(error);
            Ensure(occurrences.Length == 2 && occurrences[0].Stage == "ChildStop" &&
                occurrences[0].Failure is { } source && Only(source, marker) && Count(source, marker) == 1 &&
                ReferenceEquals(occurrences[0].Original, Read<Task>(f.Child, "_stopTask")) &&
                occurrences[1].Stage == "UncertifiedOwner" && ReferenceEquals(occurrences[1].Owner, owner) &&
                occurrences[1].Failure is null, "one original observer refund source plus missing parent certificate, no fake physical error");
            await f.ExpectOccurrences(f.Own(Read<Task>(f.Child, "_stopTask")), marker, 1);
            Ensure(Read<int>(raw, "_references") == 0 && Read<ulong>(raw, "_bytes") == 5248 && !raw.IsRefunded &&
                ReferenceEquals(ReadOptional<object>(f.Multi, "_assemblyOwners"), owner),
                "failed original last-release outcome remains charged and rooted, never decremented a second time");
            f.AcceptInspectedRetainedStop(error);
        });

    [Test]
    public Task D217OriginalPhysicalAndObserverFaultGraphsStayComplete()
        => Run(async f =>
        {
            f.HoldCall();
            var physicalA = new InvalidOperationException("d2 physical sibling A");
            var physicalB = new OperationCanceledException("d2 physical sibling B is not caller cancellation");
            var physicalSource = new AggregateException(physicalA, physicalB);
            var observerA = new InvalidOperationException("d2 observer sibling A");
            var observerB = new OperationCanceledException("d2 observer sibling B is not caller cancellation");
            var observerSource = new AggregateException(observerA, observerB);
            f.Module.RetirementCapacity.FinalRefundFailureForDiagnostics = observerSource;
            var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.OnCancellation(() => { called.TrySetResult(); throw physicalSource; });
            var waiter = await f.Result(f.Invoke(() => f.Unregister()));
            await f.Within(called.Task); f.ReleaseCall();
            await f.ExpectGraph(waiter, physicalA, physicalB, observerA, observerB);
            var owner = f.LastOwner; await f.JoinOwner(owner);
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            await f.ExpectGraph(receipt.Physical!, physicalA, physicalB);
            var originalPhysical = await f.Failure(receipt.Physical!) ?? throw new InvalidOperationException("Original physical graph missing.");
            var originalObservation = await f.Result(receipt.Observation!);
            Ensure(receipt.Physical!.IsFaulted && receipt.Observation!.IsCompletedSuccessfully &&
                Count(originalPhysical, physicalSource) == 1 && ReferenceEquals(originalObservation.Failure, observerSource) &&
                ReferenceEquals(Read<Exception>(owner, "_physicalFailure"), originalPhysical) &&
                ReferenceEquals(Read<Exception>(owner, "_observerFailure"), observerSource) &&
                receipt.HasPhysicalExit && receipt.ModuleReleased && !receipt.AccountingRefunded,
                "two original outcome channels retain both complete sibling graphs and raw OCE leaves without Task-status heuristics");
            var stop = f.Stop(); var error = await f.Failure(stop) ?? throw new InvalidOperationException("Expected original source Stop failure.");
            var occurrences = StopOccurrences(error);
            Ensure(occurrences.Length == 2 && occurrences[0].Stage == "ChildStop" && occurrences[0].Failure is { } graph &&
                OnlyAny(graph, [physicalA, physicalB, observerA, observerB]) &&
                Count(graph, originalPhysical) == 1 && Count(graph, observerSource) == 1 &&
                occurrences[1].Stage == "UncertifiedOwner" && ReferenceEquals(occurrences[1].Owner, owner) &&
                occurrences[1].Failure is null,
                "childStop owns each original source graph once, outer summary does not replay either graph");
            await f.ExpectGraph(f.Own(Read<Task>(f.Child, "_stopTask")), physicalA, physicalB, observerA, observerB);
            f.AcceptInspectedRetainedStop(error);
        });
}
