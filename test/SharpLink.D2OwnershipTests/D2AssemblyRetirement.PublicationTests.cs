using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class D2AssemblyRetirementTests
{
    [Test]
    public Task D213PublishedReplacementRetainsOldPhysicalOwner()
        => Run(async f =>
        {
            f.HoldCall(); var callback = f.NewGate(); f.OnCancellation(callback.Wait);
            var replacement = f.NewPlugin(); using var caller = new CancellationTokenSource();
            var waiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly, caller.Token)));
            await f.Within(callback.Entered.Task);
            var owner = f.LastOwner;
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            Ensure(receipt.Disposition == ChildAssemblyDisposition.SelectedCommitted &&
                Read<TaskCompletionSource<bool>>(owner, "PublicationDone").Task.IsCompletedSuccessfully &&
                f.RoutesTo(replacement.Assembly) && !f.RoutesTo(f.OldAssembly) &&
                receipt.Physical is { IsCompleted: false }, "new exact route published before old call/driver exit");
            caller.Cancel(); await f.ExpectCallerCancellation(waiter, caller.Token);
            Ensure(!Read<Task>(owner, "Worker").IsCompleted && !Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded,
                "public cancellation cannot refund or remove the durable old owner");
            callback.Release.Set();
            await f.Within(receipt.Control!.GraceDecision.Task);
            Ensure(!receipt.HasPhysicalExit && !receipt.ModuleReleased && !receipt.AccountingRefunded,
                "early grace result does not certify held old physical ownership");
            f.ReleaseCall(); await f.JoinOwner(owner);
            Ensure(receipt.HasPhysicalExit && receipt.ModuleReleased && receipt.AccountingRefunded &&
                Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded,
                "actual old-source join releases both exact child and outer inventory");
        });
    [Test]
    public Task D208RegisterRejectsForeignPublicationWindowBeforeChildCommit()
        => Run(async f =>
        {
            var replacement = f.NewPlugin(); var competing = f.NewPlugin(); var finalize = f.NewGate();
            f.ParentHook = (stage, _) => { if (stage == "BeforeDispositionFinalize") finalize.Wait(); };
            var waiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly)));
            await f.Within(finalize.Entered.Task);
            var owner = f.LastOwner; var before = f.SnapshotIdentity;
            Ensure(ReferenceEquals(ReadOptional<object>(f.Multi, "_assemblyWindow"), owner), "exact window is held");
            // The other child has no conflicting local module: this reaches the
            // coordinator commit callback rather than failing its local candidate.
            var rejected = await f.Result(f.Invoke(() => f.Multi.RegisterAssembly(f.OtherKey, competing.Assembly)));
            Ensure(!rejected.Succeeded && rejected.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState &&
                !f.OtherChild.IsDynamicAssemblyRegistered(competing.Assembly) &&
                ReferenceEquals(before, f.SnapshotIdentity), "foreign window rejects before other-child commit and outer swap");
            finalize.Release.Set();
            Ensure((await f.Result(waiter)).Succeeded, "selected original replacement completes");
            await f.JoinOwner(owner);
            Ensure(f.RoutesTo(replacement.Assembly) && !f.RoutesTo(competing.Assembly), "no rejected candidate snapshot replay");
        }, secondary: true);

    [Test]
    public Task D209CompetingAssemblyMutationCannotReplayStaleSnapshot()
        => Run(async f =>
        {
            var candidate = f.NewPlugin(); var finalize = f.NewGate();
            f.ParentHook = (stage, _) => { if (stage == "BeforeDispositionFinalize") finalize.Wait(); };
            var first = await f.Result(f.Invoke(() => f.Unregister()));
            await f.Within(finalize.Entered.Task);
            var owner = f.LastOwner; var before = f.SnapshotIdentity;
            var coalesced = await f.Result(f.Invoke(() => f.Unregister(TimeSpan.FromDays(1))));
            var rejected = await f.Result(await f.Result(f.Invoke(() => f.Replace(candidate.Assembly))));
            Ensure(!rejected.Succeeded && rejected.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState &&
                f.OwnerCount == 1 && ReferenceEquals(owner, f.LastOwner) &&
                ReferenceEquals(before, f.SnapshotIdentity) && !coalesced.IsCompleted,
                "exact unregister coalesces while unrelated replacement cannot claim or overwrite");
            finalize.Release.Set();
            Ensure((await f.Result(first)).ReferencesReleased && (await f.Result(coalesced)).ReferencesReleased,
                "both waiters consume the one original unregister outcome");
            await f.JoinOwner(owner);
            Ensure(!f.RoutesTo(f.OldAssembly) && !f.RoutesTo(candidate.Assembly), "no obsolete restoration publication");
        });

    [Test]
    public async Task D210SlotMutationAndAssemblyWindowAreMutuallyExclusive()
    {
        await Run(async f =>
        {
            var finalize = f.NewGate();
            f.ParentHook = (stage, _) => { if (stage == "BeforeDispositionFinalize") finalize.Wait(); };
            var waiter = await f.Result(f.Invoke(() => f.Unregister()));
            await f.Within(finalize.Entered.Task);
            var before = f.SnapshotIdentity;
            await AssertSlotBusy(f);
            Ensure(ReferenceEquals(before, f.SnapshotIdentity), "window excludes all slot publication");
            finalize.Release.Set();
            Ensure((await f.Result(waiter)).ReferencesReleased, "original assembly owner completes");
            await f.JoinOwner(f.LastOwner);
        });
        await Run(async f =>
        {
            var callback = f.NewGate(); var source = new HeldEmptyManifestSource(callback);
            var addedKey = new SharpLinkClusterKey("d2-added");
            var invocation = f.Invoke(() => f.Own(SharpLinkMultiClusterClientExtensions.AddClusterAsync(f.Multi, addedKey,
                builder => builder.UseTcp(System.Net.IPAddress.Loopback.ToString(), 1), null, default,
                source, FixedGeneratedClusterRouteSource.Empty).AsTask()));
            await f.Within(callback.Entered.Task);
            Ensure(Read<bool>(f.Multi, "_slotMutationInProgress"), "real slot mutation owns its admission flag");
            var rejected = await f.Result(await f.Result(f.Invoke(() => f.Unregister())));
            Ensure(!rejected.ReferencesReleased && f.OwnerCount == 0 &&
                ReadOptional<object>(f.Multi, "_assemblyWindow") is null && f.RoutesTo(f.OldAssembly),
                "existing slot mutation excludes D2 claim without losing old routes");
            callback.Release.Set();
            Ensure((await f.Result(await f.Result(invocation))).Succeeded && source.Calls == 1,
                "original slot source publishes once without replay");
            f.CaptureChildren();
            Ensure(f.WithSnapshot(snapshot => snapshot.Clusters.ContainsKey(addedKey)) && f.RoutesTo(f.OldAssembly),
                "new slot and original route coexist in the current snapshot");
        });
    }

    [Test]
    public Task D211StopSealCannotDropCommittedOuterPublication()
        => Run(async f =>
        {
            var replacement = f.NewPlugin(); var finalize = f.NewGate(); var refund = f.NewGate();
            f.ParentHook = (stage, _) =>
            {
                if (stage == "BeforeDispositionFinalize") finalize.Wait();
                if (stage == "BeforeFinalRefund") refund.Wait();
            };
            var waiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly)));
            await f.Within(finalize.Entered.Task);
            var owner = f.LastOwner; var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            Ensure(receipt.Control is { IsCommitted: true } && f.RoutesTo(f.OldAssembly),
                "actual child committed while outer mandatory swap is held");
            var stop = f.Stop();
            Ensure(Read<bool>(f.Multi, "_assemblySealed") && !stop.IsCompleted, "Stop seals without vetoing owned publication");
            finalize.Release.Set(); await f.Within(refund.Entered.Task);
            Ensure(Read<TaskCompletionSource<bool>>(owner, "PublicationDone").Task.IsCompletedSuccessfully &&
                Read<bool>(owner, "_published") && f.RoutesTo(replacement.Assembly) && !f.RoutesTo(f.OldAssembly) &&
                !stop.IsCompleted, "exact prebuilt new route exists after seal before final physical/accounting join");
            refund.Release.Set();
            Ensure((await f.Result(waiter)).Succeeded, "committed original outcome retained");
            await f.Within(stop); await f.JoinOwner(owner);
            Ensure(receipt.HasPhysicalExit && receipt.ModuleReleased && receipt.AccountingRefunded,
                "Stop joined actual selected child source and accounting");
        });

    [Test]
    public Task D212PostcommitFailureStillCompletesMandatorySwap()
        => Run(async f =>
        {
            var replacement = f.NewPlugin(); var marker = new InvalidOperationException("d2 independent finalize stage");
            f.ParentHook = (stage, _) => { if (stage == "BeforeDispositionFinalize") throw marker; };
            var waiter = await f.Result(f.Invoke(() => f.Replace(replacement.Assembly)));
            await f.ExpectFault(waiter, marker);
            var owner = f.LastOwner; await f.JoinOwner(owner);
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            Ensure(receipt.Disposition == ChildAssemblyDisposition.SelectedCommitted && Read<bool>(owner, "_published") &&
                Read<TaskCompletionSource<bool>>(owner, "PublicationDone").Task.IsCompletedSuccessfully &&
                ReadOptional<object>(f.Multi, "_assemblyWindow") is null && f.RoutesTo(replacement.Assembly) && !f.RoutesTo(f.OldAssembly),
                "independent finalize error did not skip mandatory no-throw swap/release");
            Ensure(receipt.HasPhysicalExit && receipt.ModuleReleased && receipt.AccountingRefunded &&
                Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded,
                "known independent error is not fabricated unknown resource retention");
        });

    [Test]
    public Task D230UncertifiedOwnerKeepsLegacyGlobalSlotBusy()
        => Run(async f =>
        {
            f.HoldCall();
            var waiter = await f.Result(f.Invoke(() => f.Unregister()));
            Ensure(!(await f.Result(waiter)).ReferencesReleased, "actual held call returns early grace result");
            var owner = f.LastOwner; var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            Ensure(ReadOptional<object>(f.Multi, "_assemblyWindow") is null && !receipt.HasPhysicalExit &&
                !Read<CoordinatorAssemblyCapacity>(owner, "Capacity").Refunded,
                "short window is released but exact physical/inventory ownership remains");
            var before = f.SnapshotIdentity;
            await AssertSlotBusy(f);
            Ensure(ReferenceEquals(before, f.SnapshotIdentity) && f.WithSnapshot(snapshot =>
                ReferenceEquals(snapshot.Clusters[f.Key].Client, f.Child)), "legacy cleanup cannot take the retained child slot");
            f.ReleaseCall(); await f.JoinOwner(owner);
        });

    private static async Task AssertSlotBusy(Fixture f)
    {
        var add = await f.Result(f.Own(SharpLinkMultiClusterClientExtensions.AddClusterAsync(f.Multi,
            new SharpLinkClusterKey("d2-rejected-add"), builder => builder.UseTcp(System.Net.IPAddress.Loopback.ToString(), 1),
            null, default, FixedGeneratedManifestSource.Empty, FixedGeneratedClusterRouteSource.Empty).AsTask()));
        var replace = await f.Result(f.Own(f.Multi.ReplaceClusterAsync(f.Key,
            builder => builder.UseTcp(System.Net.IPAddress.Loopback.ToString(), 1), TimeSpan.Zero).AsTask()));
        var remove = await f.Result(f.Own(f.Multi.RemoveClusterAsync(f.Key, TimeSpan.Zero).AsTask()));
        Ensure(!add.Succeeded && add.FailureCode == SharpLinkClusterMutationFailureCode.Busy &&
            !replace.Succeeded && !replace.Published && replace.FailureCode == SharpLinkClusterMutationFailureCode.Busy &&
            !remove.Succeeded && remove.FailureCode == SharpLinkClusterMutationFailureCode.Busy,
            "Add/Replace/Remove each return exact Busy without publication");
    }
    private sealed class HeldEmptyManifestSource(Gate gate) : IGeneratedManifestSource
    {
        internal int Calls;
        public IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CreateSnapshot()
        { Interlocked.Increment(ref Calls); gate.Wait(); return []; }
    }

    [Test]
    public async Task D224SelectedChildBudgetSurvivesCoordinatorD2Path()
    {
        var clock = new EntryOnlyClock();
        await Run(async f =>
        {
            f.HoldCall(); var replacement = f.NewPlugin(blocked: true);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationCount = 0;
            f.OnCancellation(() => { Interlocked.Increment(ref cancellationCount); cancelled.TrySetResult(); });
            var invocation = f.Invoke(() => f.Own(f.Multi.ReplaceAssemblyAsync(f.Key, f.OldAssembly,
                replacement.Assembly, TimeSpan.FromSeconds(5)).AsTask()));
            await f.Within(replacement.Entered.Task);
            Ensure(f.OwnerCount == 0 && clock.Timestamp == 1000,
                "actual loader is blocked after entry sampling but before coordinator election");
            clock.Timestamp = 7000; replacement.Release.TrySetResult();
            var waiter = await f.Result(invocation);
            await f.Within(cancelled.Task);
            var result = await f.Result(waiter);
            var owner = f.LastOwner; var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            var outer = Read<ClientRetirementBudget>(owner, "Budget").GetFor(f.Registry);
            var child = Read<RetirementGraceBudget>(receipt.Control!, "_budget");
            Ensure(result.Succeeded && !result.ReferencesReleased && clock.TimerCreates == 0 && cancellationCount == 1 &&
                ReferenceEquals(outer.Provider, clock) && ReferenceEquals(child.Provider, clock) &&
                outer.TimestampOrigin == 1000 && child.TimestampOrigin == 1000 &&
                outer.TimestampFrequency == 1000 && child.TimestampFrequency == 1000 &&
                outer.Duration == TimeSpan.FromSeconds(5) && child.Duration == outer.Duration,
                "original exact-child five-second entry budget expires during loader rather than restarting at election");
            f.ReleaseCall(); await f.JoinOwner(owner);
            Ensure(receipt.HasPhysicalExit && receipt.ModuleReleased && receipt.AccountingRefunded,
                "budget outcome remains independent of actual old physical release");
        }, provider: clock);
    }

    private sealed class EntryOnlyClock : TimeProvider
    {
        private long _timestamp = 1000;
        internal long Timestamp { get => Interlocked.Read(ref _timestamp); set => Interlocked.Exchange(ref _timestamp, value); }
        internal int TimerCreates;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Interlocked.Increment(ref TimerCreates); throw new InvalidOperationException("Expired original D2 budget must not create a fresh timer."); }
    }
}
