using System.Linq;
using System.Net;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class D2AssemblyRetirementTests
{
    [Test]
    public Task D233SlotCloneOldReleaseFailureKeepsBothExactCustodians()
        => Run(async f =>
        {
            var original = Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Single();
            var capacity = original.Capacity!;
            var token = Read<ColdMetadataReservation>(capacity, "_token");
            var snapshot = f.SnapshotIdentity;
            var marker = new InvalidOperationException("d2 actual old registration lease pre-effect failure");
            CoordinatorAssemblyCapacity? cloneCapacity = null;
            DynamicAssemblyRegistration? clone = null;
            var oldReleaseCalls = 0;
            f.ParentHook = (stage, selected) =>
            {
                if (stage == "AfterCloneLeaseRetain") cloneCapacity = (CoordinatorAssemblyCapacity)selected;
                if (stage != "BeforeFinalLeaseRelease" || !ReferenceEquals(selected, capacity)) return;
                oldReleaseCalls++;
                clone = Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Single();
                Ensure(!ReferenceEquals(clone, original) && ReferenceEquals(clone.Capacity, cloneCapacity),
                    "real replacement installed the independently retained clone before old release");
                throw marker;
            };
            var originalCall = f.Own(f.Multi.ReplaceClusterAsync(f.Key,
                builder => builder.UseTcp(IPAddress.Loopback.ToString(), 1), TimeSpan.Zero).AsTask());
            // Original old release and rollback clone release independently
            // report the same uncertain sibling; neither occurrence is deduped.
            await f.ExpectOccurrences(originalCall, marker, 2);
            Ensure(oldReleaseCalls == 1 && clone is not null && cloneCapacity is not null &&
                ReferenceEquals(f.SnapshotIdentity, snapshot) &&
                ReferenceEquals(Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Single(), original) &&
                Read<bool>(f.Multi, "_assemblyPublicationFailed") &&
                original.CustodyRetained && clone.CustodyRetained &&
                ReferenceEquals(original.DisposalFailure, marker) && ReferenceEquals(clone.DisposalFailure, marker),
                "failed replace restored exact source row and rooted both failed registration occurrences");
            Ensure(!Read<bool>(capacity, "_leaseReturned") && Read<bool>(cloneCapacity!, "_leaseReturned") &&
                !capacity.Refunded && !cloneCapacity!.Refunded && !token.IsRefunded &&
                Read<int>(token, "_references") == 1 && Read<ulong>(token, "_bytes") == 12544 &&
                Read<int>(Read<SharpLinkRuntimeContext>((SharpLinkClient)clone!.Slot.Client, "_runtimeContext"), "_disposed") != 0,
                "original replacement Task joined actual candidate cleanup without refunding uncertain old96");
            f.ParentHook = null;
            await AssertSlotBusy(f);
            var stop = f.Stop(); var failure = await f.Failure(stop) ?? throw new InvalidOperationException("Expected custody Stop failure.");
            var occurrences = StopOccurrences(failure);
            Ensure(occurrences.Length == 2 && occurrences.All(row => row.Stage == "RegistrationDisposal" &&
                row.Original is null && ReferenceEquals(row.Failure, marker)) &&
                occurrences.Count(row => ReferenceEquals(row.Owner, original)) == 1 &&
                occurrences.Count(row => ReferenceEquals(row.Owner, clone)) == 1 &&
                !token.IsRefunded && Read<int>(f.Multi, "_assemblyInventoryCount") == 1 &&
                original.Slot.MetadataOwner is not null && oldReleaseCalls == 1,
                "typed Stop retains each registration occurrence once and never reruns failed old release");
            f.AcceptInspectedRetainedStop(failure);
        });

    [Test]
    public Task D234SlotRemoveReleaseFailureKeepsOriginalRegistration()
        => Run(async f =>
        {
            var original = Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Single();
            var capacity = original.Capacity!; var token = Read<ColdMetadataReservation>(capacity, "_token");
            var snapshot = f.SnapshotIdentity;
            var marker = new InvalidOperationException("d2 actual remove registration lease pre-effect failure");
            var releases = 0;
            f.ParentHook = (stage, selected) =>
            {
                if (stage != "BeforeFinalLeaseRelease" || !ReferenceEquals(selected, capacity)) return;
                releases++; throw marker;
            };
            var originalCall = f.Own(f.Multi.RemoveClusterAsync(f.Key, TimeSpan.Zero).AsTask());
            await f.ExpectFault(originalCall, marker);
            Ensure(releases == 1 && ReferenceEquals(f.SnapshotIdentity, snapshot) &&
                Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Contains(original) &&
                original.CustodyRetained && ReferenceEquals(original.DisposalFailure, marker) &&
                ReferenceEquals(ReadOptional<DynamicAssemblyRegistration>(f.Multi, "_failedAssemblyRegistrations"), original) &&
                !token.IsRefunded && Read<ulong>(token, "_bytes") == 12544,
                "failed original Dispose remains linked; removal neither publishes nor drops actual96 custody");
            f.ParentHook = null;
            await AssertSlotBusy(f);
            var failure = await f.Failure(f.Stop()) ?? throw new InvalidOperationException("Expected remove custody Stop failure.");
            var rows = StopOccurrences(failure);
            Ensure(rows.Length == 1 && rows[0].Stage == "RegistrationDisposal" &&
                ReferenceEquals(rows[0].Owner, original) && ReferenceEquals(rows[0].Failure, marker) &&
                rows[0].Original is null && original.Slot.MetadataOwner is not null &&
                !capacity.Refunded && !Read<bool>(capacity, "_leaseReturned") &&
                Read<int>(f.Multi, "_assemblyInventoryCount") == 1 && releases == 1,
                "Stop joins original child but retains failed registration and does not retry its lease release");
            f.AcceptInspectedRetainedStop(failure);
        });

    [Test]
    public Task D235UnpublishedRegistrationRollbackRetainsExact96Custody()
        => Run(async f =>
        {
            var assembly = typeof(SharpLink.MultiClusterTest.Contracts.IOrdersContract).Assembly;
            var registry = Read<ClientAssemblyRegistry>(f.OtherChild, "_assemblyRegistry");
            var generation = Read<long>(registry, "_generation");
            var snapshot = f.SnapshotIdentity;
            var marker = new InvalidOperationException("d2 unpublished registration rollback lease failure");
            CoordinatorAssemblyCapacity? selectedCapacity = null;
            SharpLinkAssemblyRegistrationResult? independent = null;
            var detachCalls = 0; var releaseCalls = 0;
            f.ParentHook = (stage, selected) =>
            {
                if (stage == "BeforeOriginalDetach")
                {
                    detachCalls++; selectedCapacity = (CoordinatorAssemblyCapacity)selected;
                    // Actual competing child publication changes the final
                    // Commit's captured generation; no fake rejection callback.
                    independent = f.OtherChild.RegisterAssembly(f.OldAssembly);
                    Ensure(independent.Value.Succeeded && Read<long>(registry, "_generation") == generation + 1,
                        "actual independent child registration invalidated the prepared candidate generation");
                }
                if (stage == "BeforeFinalLeaseRelease" && ReferenceEquals(selected, selectedCapacity))
                { releaseCalls++; throw marker; }
            };
            var result = await f.Result(f.Invoke(() => f.Multi.RegisterAssembly(f.OtherKey, assembly)));
            Ensure(!result.Succeeded && result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                independent is { Succeeded: true } && detachCalls == 1 && releaseCalls == 1 &&
                !f.OtherChild.IsDynamicAssemblyRegistered(assembly) && f.OtherChild.IsDynamicAssemblyRegistered(f.OldAssembly) &&
                ReferenceEquals(f.SnapshotIdentity, snapshot) && !f.RoutesTo(assembly),
                "real final-gate rejection plus actual rollback failure cannot publish the new child/parent route");
            var retained = Read<DynamicAssemblyRegistration>(f.Multi, "_failedAssemblyRegistrations");
            var token = Read<ColdMetadataReservation>(selectedCapacity!, "_token");
            Ensure(ReferenceEquals(retained.Assembly, assembly) && ReferenceEquals(retained.Capacity, selectedCapacity) &&
                retained.CustodyRetained && ReferenceEquals(retained.DisposalFailure, marker) &&
                retained.SnapshotFailure is null && !Read<List<DynamicAssemblyRegistration>>(f.Multi, "_dynamicRegistrations").Contains(retained) &&
                !selectedCapacity!.Refunded && !token.IsRefunded && Read<ulong>(token, "_bytes") == 12544,
                "unpublished row still roots its exact failed96; successful snapshot cleanup is not fabricated failure");
            f.ParentHook = null;
            await AssertSlotBusy(f);
            var failure = await f.Failure(f.Stop()) ?? throw new InvalidOperationException("Expected unpublished custody Stop failure.");
            var rows = StopOccurrences(failure);
            Ensure(rows.Length == 1 && rows[0].Stage == "RegistrationDisposal" &&
                ReferenceEquals(rows[0].Owner, retained) && ReferenceEquals(rows[0].Failure, marker) && rows[0].Original is null &&
                retained.Slot.MetadataOwner is not null && !token.IsRefunded && releaseCalls == 1 &&
                Read<int>(f.Multi, "_assemblyInventoryCount") == 2,
                "Stop retains unpublished failure and live old registration inventory without false refund or repeated Dispose");
            f.AcceptInspectedRetainedStop(failure);
        }, secondary: true);
}
