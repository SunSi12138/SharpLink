using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class D2AssemblyRetirementTests
{
    [Test]
    public Task D231LivePreparationDoesNotRetainDetachedOuter96Charge()
        => Run(async f =>
        {
            var registration = Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Single();
            var preparation = ((PreparedGeneratedManifest)registration.Manifest).Preparation;
            var replacement = f.NewPlugin();
            var marker = new InvalidOperationException("d2 accounting abort");
            var refund = f.NewGate();
            f.ChildHook = stage => { if (stage == "ObserverRooted") throw marker; };
            f.ParentHook = (stage, _) =>
            {
                if (stage != "BeforeFinalRefund") return;
                refund.Wait();
            };
            var waiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly)));
            await f.Within(refund.Entered.Task);
            // The synchronous public invocation has returned, so its using tail
            // cannot race this isolated authority delta while the refund is held.
            var beforeRefund = f.Authority.Bytes;
            var owner = f.LastOwner; var capacity = Read<CoordinatorAssemblyCapacity>(owner, "Capacity");
            var token = Read<ColdMetadataReservation>(capacity, "_token");
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            Ensure(receipt.AbortCertified && token.IsDetached && token.OriginalReleaseSucceeded &&
                !token.IsRefunded && !capacity.Refunded && Read<ulong>(token, "_bytes") == 12544 &&
                Read<int>(preparation, "_references") > 0,
                "original detach is not refund; live preparation no longer owns the96 reference");
            refund.Release.Set();
            Ensure(!(await f.Result(waiter)).Succeeded, "actual selected Abort retains rejection");
            await f.JoinOwner(owner);
            Ensure(capacity.Refunded && token.IsRefunded && f.Authority.Bytes == beforeRefund - 12544 &&
                Read<int>(preparation, "_references") > 0 && ReferenceEquals(registration.Manifest,
                    Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Single().Manifest),
                "sole final lease refunded exactly96 while live registration preparation remains usable");
            f.ChildHook = null; f.ParentHook = null;
            var next = await f.Result(f.Invoke(() => f.Unregister()));
            Ensure((await f.Result(next)).ReferencesReleased, "subsequent exact cold election succeeds");
            var nextOwner = f.LastOwner; await f.JoinOwner(nextOwner);
            var nextCapacity = Read<CoordinatorAssemblyCapacity>(nextOwner, "Capacity");
            var nextToken = Read<ColdMetadataReservation>(nextCapacity, "_token");
            Ensure(!ReferenceEquals(owner, nextOwner) && !ReferenceEquals(capacity, nextCapacity) &&
                !ReferenceEquals(token, nextToken) && nextToken.IsDetached && nextCapacity.Refunded &&
                token.IsRefunded && Read<ulong>(token, "_bytes") == 0,
                "new attempt gets a new detached token only after old actual refund, never a reset stage");
        });

    [Test]
    public async Task D228DormantCloneOwnsIndependentLeasesWithoutActiveOwner()
    {
        await Run(async f =>
        {
            var registration = Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Single();
            var capacity = registration.Capacity!;
            var token = Read<ColdMetadataReservation>(capacity, "_token");
            var preparation = ((PreparedGeneratedManifest)registration.Manifest).Preparation;
            var metadata = Read<IDisposable>(registration, "_metadata");
            var before = f.Authority.Bytes;
            var clone = f.OwnResource(registration with { Slot = registration.Slot });
            Ensure(!ReferenceEquals(clone, registration) && ReferenceEquals(clone.Manifest, registration.Manifest) &&
                !ReferenceEquals(Read<IDisposable>(clone, "_metadata"), metadata) &&
                !ReferenceEquals(clone.Capacity, capacity) &&
                ReferenceEquals(Read<ColdMetadataReservation>(clone.Capacity!, "_token"), token) &&
                !clone.Capacity!.CanClaim && !capacity.CanClaim && f.OwnerCount == 0 &&
                f.Authority.Bytes == before, "actual record copy retains independent leases, not a new charge or active owner");
            clone.Dispose();
            Ensure(!clone.Capacity.Refunded && capacity.CanClaim && !token.IsRefunded &&
                f.Authority.Bytes == before && Read<int>(preparation, "_references") > 0,
                "clone nonfinal decrement is not refund; original live registration remains usable");
            var waiter = await f.Result(f.Invoke(() => f.Unregister()));
            Ensure((await f.Result(waiter)).ReferencesReleased, "surviving registration can select its exact owner");
            await f.JoinOwner(f.LastOwner);
            Ensure(token.IsRefunded && capacity.Refunded, "the actual surviving sole lease refunds exactly once");
        });
        foreach (var stage in new[] { "BeforeCloneLeaseRetain", "AfterCloneLeaseRetain" })
            await Run(async f =>
            {
                var registration = Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Single();
                var capacity = registration.Capacity!; var manifest = registration.Manifest;
                var token = Read<ColdMetadataReservation>(capacity, "_token");
                var preparation = ((PreparedGeneratedManifest)manifest).Preparation;
                var references = Read<int>(preparation, "_references"); var before = f.Authority.Bytes;
                var marker = new InvalidOperationException("d2 exact clone stage");
                CoordinatorAssemblyCapacity? startedClone = null; var calls = 0;
                f.ParentHook = (actual, selected) =>
                {
                    if (actual != stage) return;
                    Interlocked.Increment(ref calls);
                    if (stage == "AfterCloneLeaseRetain") startedClone = (CoordinatorAssemblyCapacity)selected;
                    throw marker;
                };
                var attempt = f.Invoke(() => f.OwnResource(registration with { Slot = registration.Slot }));
                await f.ExpectFault(attempt, marker);
                Ensure(calls == 1 && ReferenceEquals(registration.Manifest, manifest) && capacity.CanClaim &&
                    !capacity.Refunded && !token.IsRefunded && Read<int>(preparation, "_references") == references &&
                    f.Authority.Bytes == before && f.OwnerCount == 0,
                    "failed actual clone construction rolls back only its own retains and preserves original identity");
                Ensure(stage != "AfterCloneLeaseRetain" || (startedClone is not null &&
                    Read<bool>(startedClone, "_leaseReturned") && !startedClone.Refunded),
                    "post-retain fault observes real clone lease return, not final refund");
                f.ParentHook = null;
            });
    }

    [Test]
    public Task D232CloneDecrefCannotFabricateFinalRefund()
        => Run(async f =>
        {
            var registration = Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Single();
            var capacity = registration.Capacity!; var token = Read<ColdMetadataReservation>(capacity, "_token");
            var primary = new InvalidOperationException("d2 actual clone after-retain stage");
            var cleanup = new InvalidOperationException("d2 pre-effect partial clone lease-return failure");
            CoordinatorAssemblyCapacity? clone = null; var retainCalls = 0; var releaseCalls = 0;
            f.ParentHook = (stage, selected) =>
            {
                if (stage == "AfterCloneLeaseRetain")
                { Interlocked.Increment(ref retainCalls); clone = (CoordinatorAssemblyCapacity)selected; throw primary; }
                if (stage == "BeforeFinalLeaseRelease" && ReferenceEquals(selected, clone))
                { Interlocked.Increment(ref releaseCalls); throw cleanup; }
            };
            var attempted = f.Invoke(() => f.OwnResource(registration with { Slot = registration.Slot }));
            await f.ExpectGraph(attempted, primary, cleanup);
            Ensure(clone is not null && ReferenceEquals(ReadOptional<CoordinatorAssemblyCapacity>(capacity, "_sibling"), clone) &&
                ReferenceEquals(Read<ColdMetadataReservation>(clone, "_token"), token) &&
                !Read<bool>(clone, "_leaseReturned") && !clone.Refunded && !capacity.CanClaim &&
                !token.IsRefunded && Read<ulong>(token, "_bytes") == 12544 && retainCalls == 1 && releaseCalls == 1,
                "actual partial clone remains rooted and charged; failed lease return cannot allow selection or invent a refund");
            f.ParentHook = null;
            var stop = f.Stop(); var failure = await f.Failure(stop) ?? throw new InvalidOperationException("Expected failed clone Stop custody.");
            var occurrences = StopOccurrences(failure);
            Ensure(occurrences.Length == 1 && occurrences[0].Stage == "RegistrationCleanup" &&
                ReferenceEquals(occurrences[0].Owner, registration) && ReferenceEquals(occurrences[0].Failure, cleanup) &&
                occurrences[0].Original is null,
                "real registration disposal preserves failed sibling custody without fabricated physical Task");
            Ensure(Read<bool>(capacity, "_leaseReturned") && !capacity.Refunded && !clone!.Refunded &&
                !token.IsRefunded && Read<ulong>(token, "_bytes") == 12544 && Read<int>(token, "_references") == 1 &&
                Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Contains(registration) &&
                ReferenceEquals(ReadOptional<CoordinatorAssemblyCapacity>(capacity, "_sibling"), clone) &&
                releaseCalls == 1, "old successful nonfinal decrement is not final refund and failed clone is never blindly retried");
            f.AcceptInspectedRetainedStop(failure);
        });

    [Test]
    public async Task D229AbortRefundPrecedesFreshStageAndCapacity()
    {
        // The successful successor is independently covered by D2-06. These
        // two actual admission boundaries prohibit a fresh parent stage.
        foreach (var failRefund in new[] { false, true })
            await Run(async f =>
            {
                var replacement = f.NewPlugin(); var construction = new InvalidOperationException("d2 held direct Abort");
                var refundFailure = new InvalidOperationException("d2 old96 pre-effect refund failure");
                var directGate = f.NewGate(); var refundGate = f.NewGate();
                var parentSelected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var mailbox = new ChildAssemblySelectionMailbox(static () => { }, _ => directGate.Wait());
                f.ChildHook = stage => { if (stage == "ObserverRooted") throw construction; };
                var sourceInvocation = f.Invoke(() =>
                {
                    var capability = (ISharpLinkPreparedAssemblyClient)f.Child;
                    var budget = capability.CaptureRetirementBudget(TimeSpan.Zero);
                    using var preparation = Read<SharpLinkRuntimeContext>(f.Child, "_runtimeContext").Domain.BeginPreparation();
                    Ensure(SharpLinkAssemblyManifestLoader.TryLoad(replacement.Assembly, preparation, out var prepared).Succeeded,
                        "real candidate prepared in the exact child authority");
                    return f.Own(capability.ReplacePreparedAssemblyAsync(f.OldAssembly, prepared!, budget, mailbox).AsTask());
                });
                await f.Within(directGate.Entered.Task);
                f.Own(mailbox.Physical!); f.Own(mailbox.Observation!); f.Own(mailbox.Control!.CreatedDriver!.Task);
                f.ChildHook = null;
                f.ParentHook = (stage, _) =>
                {
                    if (stage == "BeforeDispositionFinalize") parentSelected.TrySetResult();
                    if (failRefund && stage == "BeforeFinalRefund") { refundGate.Wait(); throw refundFailure; }
                };
                var original = await f.Result(f.Invoke(() => f.Unregister()));
                await f.Within(parentSelected.Task);
                var owner = f.LastOwner; var publicReceipt = Read<object>(owner, "Public");
                var capacity = Read<CoordinatorAssemblyCapacity>(owner, "Capacity");
                var token = Read<ColdMetadataReservation>(capacity, "_token");
                var repeated = await f.Result(f.Invoke(() => f.Unregister(TimeSpan.FromDays(1))));
                Task? stop = null;
                if (!failRefund)
                {
                    stop = f.Stop();
                    Ensure(Read<bool>(f.Multi, "_assemblySealed") && !stop.IsCompleted,
                        "Stop seal precedes Abort activation and any possible successor election");
                }
                directGate.Release.Set();
                Ensure(!(await f.Result(await f.Result(sourceInvocation))).Succeeded, "original direct child rejection remains unchanged");
                if (failRefund)
                {
                    await f.Within(refundGate.Entered.Task);
                    Ensure(mailbox.AbortCertified && !capacity.Refunded && f.OwnerCount == 1 &&
                        !original.IsCompleted && !repeated.IsCompleted, "actual Abort join is not old96 refund");
                    refundGate.Release.Set();
                    await f.ExpectFault(original, refundFailure); await f.ExpectFault(repeated, refundFailure);
                    await f.JoinOwner(owner);
                    stop = f.Stop(); var error = await f.Failure(stop) ?? throw new InvalidOperationException("Expected retained old96 failure.");
                    var occurrences = StopOccurrences(error);
                    Ensure(occurrences.Length == 2 && occurrences[0].Stage == "OuterStage" &&
                        occurrences[0].OuterStageIndex == 14 && ReferenceEquals(occurrences[0].Owner, owner) &&
                        ReferenceEquals(occurrences[0].Failure, refundFailure) && occurrences[1].Stage == "UncertifiedOwner" &&
                        ReferenceEquals(occurrences[1].Owner, owner) && occurrences[1].Failure is null &&
                        !capacity.Refunded && !token.IsRefunded && Read<ulong>(token, "_bytes") == 12544,
                        "failed old refund forbids new unit, preserving actual failed typed inventory");
                    f.AcceptInspectedRetainedStop(error);
                }
                else
                {
                    Ensure(!(await f.Result(original)).ReferencesReleased && !(await f.Result(repeated)).ReferencesReleased,
                        "sealed parent reports joined Abort without pretending a fresh parent unregister occurred");
                    await f.Within(stop!); await f.JoinOwner(owner);
                    Ensure(capacity.Refunded && token.IsRefunded, "sealed certified old owner really refunds");
                }
                Ensure(f.OwnerCount == 1 && ReferenceEquals(Read<object>(owner, "Public"), publicReceipt) &&
                    ReadOptional<object>(owner, "SuccessorCandidate") is null,
                    "neither Stop nor failure creates a hidden successor, waiter re-election or forwarded public task");
            });
    }
}
