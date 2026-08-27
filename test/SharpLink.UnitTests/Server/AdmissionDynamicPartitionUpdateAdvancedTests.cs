using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicPartitionUpdateAdvancedTests
{
    private static readonly Func<SharpLinkAdmissionContext, string?> ConnectionSelector =
        static context => context.ConnectionId;
    private static readonly Func<SharpLinkAdmissionContext, string?> ReplacementSelector =
        static context => $"replacement:{context.ConnectionId}";
    private static readonly Func<SharpLinkAdmissionContext, string?> ConstantSelectorA =
        static _ => "42";
    private static readonly Func<SharpLinkAdmissionContext, string?> ConstantSelectorB =
        static _ => "42";
    private static readonly Func<SharpLinkAdmissionContext, string?> DefaultSelectorA =
        static _ => null;
    private static readonly Func<SharpLinkAdmissionContext, string?> DefaultSelectorB =
        static _ => null;

    [Test]
    public async Task ShrinkRaceShouldRejectPausedMissingKeyAfterTargetCommits()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 2, permitLimit: 1));
        var source = Current(server);
        var pool = source.Controller.PartitionStateForTests!;

        var existing = await source.Controller.AcquireAsync(
            Context("tenant-a"), 1, false, CancellationToken.None);
        Ensure(existing.IsAcquired, "source namespace must contain one entry before the race");
        existing.Lease!.Dispose();

        using var keyResolved = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var claimed = 0;
        pool.AfterKeyResolvedBeforeEntryLockForTests = () =>
        {
            if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0)
                return;
            keyResolved.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("partition create-race barrier timed out");
        };

        try
        {
            var pending = Task.Run(async () => await source.Controller.AcquireAsync(
                Context("tenant-b"), 1, false, CancellationToken.None));
            Ensure(keyResolved.Wait(TimeSpan.FromSeconds(5)),
                "missing-key request must pause after selector resolution and before entry locking");

            publicServer.UpdateAdmissionControl(options =>
                ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 1, permitLimit: 1));
            Ensure(pool.MaxPartitionsForTests == 1 && pool.Count == 1,
                "shrink must commit while the missing-key request is paused");

            resume.Set();
            var decision = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!decision.IsAcquired && decision.Reason == "partition_capacity" && pool.Count == 1,
                "paused request must re-authorize against the committed target and cannot insert entry #2");
        }
        finally
        {
            pool.AfterKeyResolvedBeforeEntryLockForTests = null;
            resume.Set();
        }
    }

    [Test]
    public async Task PartitionConcurrencyShrinkShouldPreserveHoldersAndQueuedWaiter()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            ConfigureQueue(options, TimeSpan.FromMinutes(1));
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 8, permitLimit: 3);
        });
        var source = Current(server);
        var context = Context("tenant-a");

        var first = await source.Controller.AcquireAsync(context, 1, true, CancellationToken.None);
        var second = await source.Controller.AcquireAsync(context, 1, true, CancellationToken.None);
        var third = await source.Controller.AcquireAsync(context, 1, true, CancellationToken.None);
        var queued = source.Controller.AcquireAsync(context, 1, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => source.Kernel.QueuedCalls == 1,
            "fourth partition request must be queued before shrink");

        publicServer.UpdateAdmissionControl(options =>
        {
            ConfigureQueue(options, TimeSpan.FromMinutes(1));
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 8, permitLimit: 1);
        });
        var replacement = Current(server);
        var rejected = await replacement.Controller.AcquireAsync(
            context, 1, false, CancellationToken.None);
        Ensure(!rejected.IsAcquired && rejected.Reason == "concurrency" && !queued.IsCompleted,
            "3 -> 1 must preserve all holders and the queued waiter while rejecting new work");

        first.Lease!.Dispose();
        Ensure(!queued.IsCompleted, "release at active=2 must not grant below the shrunken target");
        second.Lease!.Dispose();
        Ensure(!queued.IsCompleted, "release at active=1 still leaves no free target capacity");
        third.Lease!.Dispose();

        var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(admitted.IsAcquired,
            "queued waiter must survive shrink and enter after natural releases reach the target");
        admitted.Lease!.Dispose();
        Ensure(source.Kernel.QueuedCalls == 0 && source.Kernel.QueuedBytes == 0 &&
               source.Kernel.ActivePermits == 0,
            "partition shrink must drain outer queue and permit accounting exactly once");
    }

    [Test]
    public async Task ExistingAndFutureKeysShouldUseUpdatedConcurrencyTargetWithoutSplitting()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 4, permitLimit: 1));
        var source = Current(server);
        var pool = source.Controller.PartitionStateForTests!;

        var oldHolder = await source.Controller.AcquireAsync(
            Context("tenant-a"), 1, false, CancellationToken.None);
        Ensure(oldHolder.IsAcquired, "existing key must hold one source permit");

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 4, permitLimit: 2));
        var replacement = Current(server);
        Ensure(ReferenceEquals(pool, replacement.Controller.PartitionStateForTests),
            "same selector concurrency update must preserve the namespace pool");

        var existingSecond = await replacement.Controller.AcquireAsync(
            Context("tenant-a"), 1, false, CancellationToken.None);
        var existingThird = await replacement.Controller.AcquireAsync(
            Context("tenant-a"), 1, false, CancellationToken.None);
        Ensure(existingSecond.IsAcquired && !existingThird.IsAcquired && existingThird.Reason == "concurrency",
            "existing key must expose only the one-permit delta after 1 -> 2");

        var futureFirst = await replacement.Controller.AcquireAsync(
            Context("tenant-b"), 1, false, CancellationToken.None);
        var futureSecond = await replacement.Controller.AcquireAsync(
            Context("tenant-b"), 1, false, CancellationToken.None);
        var futureThird = await replacement.Controller.AcquireAsync(
            Context("tenant-b"), 1, false, CancellationToken.None);
        Ensure(futureFirst.IsAcquired && futureSecond.IsAcquired &&
               !futureThird.IsAcquired && futureThird.Reason == "concurrency",
            "new key created after publication must start directly with the N+1 target");

        oldHolder.Lease!.Dispose();
        existingSecond.Lease!.Dispose();
        futureFirst.Lease!.Dispose();
        futureSecond.Lease!.Dispose();
    }

    [Test]
    [Arguments(PartitionRateKind.FixedWindow)]
    [Arguments(PartitionRateKind.SlidingWindow)]
    public async Task PartitionWindowRateIncreaseShouldExposeOnlyDeltaQuota(PartitionRateKind kind)
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionRate(options, ConnectionSelector, kind, permitLimit: 1));
        var source = Current(server);
        var context = Context("tenant-a");

        var consumed = await source.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(consumed.IsAcquired, $"{kind}: source permit must be consumed before update");
        consumed.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionRate(options, ConnectionSelector, kind, permitLimit: 2));
        var replacement = Current(server);
        var delta = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        var exhausted = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(delta.IsAcquired && !exhausted.IsAcquired && exhausted.Reason == "rate",
            $"{kind}: 1 -> 2 may expose one delta permit but must not restart a fresh two-permit policy");
        delta.Lease!.Dispose();
    }

    [Test]
    [Arguments(PartitionRateKind.TokenBucket, PartitionRateKind.FixedWindow)]
    [Arguments(PartitionRateKind.FixedWindow, PartitionRateKind.SlidingWindow)]
    [Arguments(PartitionRateKind.SlidingWindow, PartitionRateKind.TokenBucket)]
    public async Task PartitionRateAlgorithmReplacementShouldCarryRecentConsumption(
        PartitionRateKind sourceKind,
        PartitionRateKind targetKind)
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionRate(options, ConnectionSelector, sourceKind, permitLimit: 1));
        var source = Current(server);
        var context = Context("tenant-a");

        var consumed = await source.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(consumed.IsAcquired, $"{sourceKind}: source quota must be consumed before replacement");
        consumed.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionRate(options, ConnectionSelector, targetKind, permitLimit: 1));
        var replacement = Current(server);
        var attempt = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(!attempt.IsAcquired && attempt.Reason == "rate",
            $"{sourceKind}->{targetKind}: replacement must carry a conservative debt barrier rather than a fresh quota");
    }

    [Test]
    public async Task EqualLookingKeysAcrossSelectorGenerationsShouldNeverAlias()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConstantSelectorA, maxPartitions: 1, permitLimit: 1));
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "test must retain the old selector generation");
        var oldPool = source.Controller.PartitionStateForTests!;
        var oldHolder = await source.Controller.AcquireAsync(
            Context("ignored"), 1, false, CancellationToken.None);
        Ensure(oldHolder.IsAcquired, "old selector/key must hold its only permit");

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConstantSelectorB, maxPartitions: 1, permitLimit: 1));
        var replacement = Current(server);
        var newPool = replacement.Controller.PartitionStateForTests!;
        var newHolder = await replacement.Controller.AcquireAsync(
            Context("ignored"), 1, false, CancellationToken.None);
        Ensure(!ReferenceEquals(oldPool, newPool) && newHolder.IsAcquired,
            "two selector generations returning the same visible key '42' must have independent namespace state");

        oldHolder.Lease!.Dispose();
        newHolder.Lease!.Dispose();
        source.ReleaseUse();
    }

    [Test]
    public async Task DefaultKeyAcrossSelectorGenerationsShouldNeverAlias()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, DefaultSelectorA, maxPartitions: 1, permitLimit: 1));
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "test must retain the old default-key namespace");
        var oldPool = source.Controller.PartitionStateForTests!;
        var oldHolder = await source.Controller.AcquireAsync(
            Context("ignored"), 1, false, CancellationToken.None);

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, DefaultSelectorB, maxPartitions: 1, permitLimit: 1));
        var replacement = Current(server);
        var newPool = replacement.Controller.PartitionStateForTests!;
        var newHolder = await replacement.Controller.AcquireAsync(
            Context("ignored"), 1, false, CancellationToken.None);
        Ensure(oldHolder.IsAcquired && newHolder.IsAcquired && !ReferenceEquals(oldPool, newPool),
            "default/fallback key identity must be scoped to its selector namespace generation");

        oldHolder.Lease!.Dispose();
        newHolder.Lease!.Dispose();
        source.ReleaseUse();
    }

    [Test]
    public async Task RepeatedSelectorReplacementShouldNotReuseHistoricalMatchingNamespace()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConstantSelectorA, maxPartitions: 2, permitLimit: 1));
        var first = Current(server);
        Ensure(first.TryAcquireUse(), "first selector generation must be retained");
        var firstPool = first.Controller.PartitionStateForTests!;
        var firstLease = await first.Controller.AcquireAsync(
            Context("ignored"), 1, false, CancellationToken.None);

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConstantSelectorB, maxPartitions: 2, permitLimit: 1));
        var second = Current(server);
        Ensure(second.TryAcquireUse(), "second selector generation must be retained");
        var secondPool = second.Controller.PartitionStateForTests!;
        var secondLease = await second.Controller.AcquireAsync(
            Context("ignored"), 1, false, CancellationToken.None);

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConstantSelectorA, maxPartitions: 2, permitLimit: 1));
        var third = Current(server);
        var thirdPool = third.Controller.PartitionStateForTests!;
        Ensure(!ReferenceEquals(firstPool, secondPool) &&
               !ReferenceEquals(firstPool, thirdPool) &&
               !ReferenceEquals(secondPool, thirdPool) &&
               first.Kernel.PartitionStateCount == 3,
            "A -> B -> A must create a new current A namespace rather than selecting historical A by matching config");

        firstLease.Lease!.Dispose();
        secondLease.Lease!.Dispose();
        first.ReleaseUse();
        second.ReleaseUse();
        Ensure(third.Kernel.PartitionStateCount == 1,
            "retired selector generations must reclaim after their final old users drain");
    }

    [Test]
    public async Task RemoveReaddDisableEnableShouldReuseLatestCurrentNamespaceOnly()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            options.Global.UseConcurrency(8);
            ConfigurePartitionRate(options, ConnectionSelector, PartitionRateKind.TokenBucket, permitLimit: 1);
        });
        var historical = Current(server);
        Ensure(historical.TryAcquireUse(), "historical namespace must remain alive across remove/re-add");
        var historicalPool = historical.Controller.PartitionStateForTests!;

        publicServer.UpdateAdmissionControl(options => options.Global.UseConcurrency(8));
        Ensure(Current(server).Controller.PartitionStateForTests is null,
            "complete candidate omission must remove partitioning from the current publication");

        publicServer.UpdateAdmissionControl(options =>
        {
            options.Global.UseConcurrency(8);
            ConfigurePartitionRate(options, ConnectionSelector, PartitionRateKind.TokenBucket, permitLimit: 1);
        });
        var current = Current(server);
        var currentPool = current.Controller.PartitionStateForTests!;
        Ensure(!ReferenceEquals(historicalPool, currentPool) && current.Kernel.PartitionStateCount == 2,
            "re-add must create current namespace B instead of reattaching historical A");

        var consumed = await current.Controller.AcquireAsync(
            Context("tenant-a"), 1, false, CancellationToken.None);
        Ensure(consumed.IsAcquired, "current B quota must be consumed before Disable");
        consumed.Lease!.Dispose();
        Ensure(current.TryAcquireUse(), "current B namespace must remain live across Disable");

        publicServer.DisableAdmissionControl();
        publicServer.EnableAdmissionControl(options =>
        {
            options.Global.UseConcurrency(8);
            ConfigurePartitionRate(options, ConnectionSelector, PartitionRateKind.TokenBucket, permitLimit: 1);
        });
        var reenabled = Current(server);
        Ensure(ReferenceEquals(currentPool, reenabled.Controller.PartitionStateForTests),
            "compatible re-enable must bind the latest current B namespace while it remains live");
        var exhausted = await reenabled.Controller.AcquireAsync(
            Context("tenant-a"), 1, false, CancellationToken.None);
        Ensure(!exhausted.IsAcquired && exhausted.Reason == "rate",
            "compatible re-enable must not split or reset the consumed current partition quota");

        current.ReleaseUse();
        historical.ReleaseUse();
        Ensure(reenabled.Kernel.PartitionStateCount == 1,
            "historical A must reclaim while re-enabled B remains authoritative");
    }

    [Test]
    [NotInParallel]
    public async Task UpdateLosingToDisableShouldLeavePartitionTargetsUntouched()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 2, permitLimit: 1));
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "source namespace must be retained while Disable wins");
        var pool = source.Controller.PartitionStateForTests!;
        using var atWriter = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        try
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = (owner, candidate) =>
            {
                if (!ReferenceEquals(owner, server) || candidate is null)
                    return;
                atWriter.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("partition update-vs-disable barrier timed out");
            };

            var update = Task.Run(() => CaptureFailure(() => publicServer.UpdateAdmissionControl(options =>
                ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 1, permitLimit: 2))));
            Ensure(atWriter.Wait(TimeSpan.FromSeconds(5)),
                "partition candidate must be fully prepared before Disable publishes");
            Ensure(pool.MaxPartitionsForTests == 2,
                "speculative candidate preparation must not mutate live MaxPartitions");

            publicServer.DisableAdmissionControl();
            release.Set();
            var failure = await update.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(failure is InvalidOperationException && pool.MaxPartitionsForTests == 2,
                "Update losing to Disable must fail exact-source validation with no partition target mutation");
        }
        finally
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = null;
            release.Set();
            source.ReleaseUse();
        }
    }

    [Test]
    [NotInParallel]
    public async Task UpdateLosingToStopShouldLeavePartitionTargetsUntouchedAndDrain()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 2, permitLimit: 1));
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "source namespace must remain inspectable while Stop seals publication");
        var pool = source.Controller.PartitionStateForTests!;
        var kernel = source.Kernel;
        using var atWriter = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        try
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = (owner, candidate) =>
            {
                if (!ReferenceEquals(owner, server) || candidate is null)
                    return;
                atWriter.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("partition update-vs-stop barrier timed out");
            };

            var update = Task.Run(() => CaptureFailure(() => publicServer.UpdateAdmissionControl(options =>
                ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 1, permitLimit: 2))));
            Ensure(atWriter.Wait(TimeSpan.FromSeconds(5)),
                "partition candidate must reach the deterministic pre-writer barrier");

            var stop = server.StopAsync(TimeSpan.Zero).AsTask();
            await WaitUntilAsync(() => kernel.IsDraining,
                "Stop must seal Admission publication before the prepared update resumes");
            Ensure(pool.MaxPartitionsForTests == 2,
                "Stop seal must observe the unmodified source partition target");
            release.Set();

            var failure = await update.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(failure is InvalidOperationException && pool.MaxPartitionsForTests == 2,
                "prepared update must not mutate partition state after Stop seal");
            source.ReleaseUse();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0 &&
                   kernel.PartitionStateCount == 0 && kernel.PartitionEntryCount == 0 &&
                   kernel.PartitionRuntimeGenerationCount == 0 && kernel.QueuedCalls == 0 &&
                   kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
                "Stop must drain current/retired partition generations and accounting to zero");
        }
        finally
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = null;
            release.Set();
            if (!source.IsReclaimed)
                source.ReleaseUse();
        }
    }

    [Test]
    [NotInParallel]
    public async Task MultiTargetCommitShouldHideIntermediatePartitionEpochFromReaders()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            options.Global.UseConcurrency(1);
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 2, permitLimit: 1);
        });
        var source = Current(server);
        var pool = source.Controller.PartitionStateForTests!;
        var existing = await source.Controller.AcquireAsync(
            Context("tenant-a"), 1, false, CancellationToken.None);
        Ensure(existing.IsAcquired, "source namespace must contain one retained entry");
        existing.Lease!.Dispose();

        using var commitPaused = new ManualResetEventSlim();
        using var releaseCommit = new ManualResetEventSlim();
        using var readerObservedTransition = new ManualResetEventSlim();
        var paused = 0;
        source.Kernel.AfterConcurrencyResizeForTests = (_, _) =>
        {
            if (Interlocked.CompareExchange(ref paused, 1, 0) != 0)
                return;
            commitPaused.Set();
            if (!releaseCommit.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("multi-target commit barrier timed out");
        };
        source.Kernel.ConcurrencyTargetTransitionObservedForTests = () => readerObservedTransition.Set();

        try
        {
            var update = Task.Run(() => publicServer.UpdateAdmissionControl(options =>
            {
                options.Global.UseConcurrency(2);
                ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 1, permitLimit: 2);
            }));
            Ensure(commitPaused.Wait(TimeSpan.FromSeconds(5)),
                "writer must pause after live partition target changes but before target-version commit closes");
            Ensure(pool.MaxPartitionsForTests == 1,
                "test must pause inside the intentionally reader-hidden commit epoch");

            var reader = Task.Run(async () => await source.Controller.AcquireAsync(
                Context("tenant-b"), 1, false, CancellationToken.None));
            Ensure(readerObservedTransition.Wait(TimeSpan.FromSeconds(5)) && !reader.IsCompleted,
                "partition reader must observe the open target epoch and remain blocked from mixed policy state");

            releaseCommit.Set();
            await update.WaitAsync(TimeSpan.FromSeconds(5));
            var decision = await reader.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!decision.IsAcquired && decision.Reason == "partition_capacity" && pool.Count == 1,
                "reader must resume only on the complete N+1 epoch and reject the missing key under MaxPartitions=1");
        }
        finally
        {
            source.Kernel.AfterConcurrencyResizeForTests = null;
            source.Kernel.ConcurrencyTargetTransitionObservedForTests = null;
            releaseCommit.Set();
        }
    }

    [Test]
    public async Task AcquireRacingIdleReclaimShouldSafelyRecreateEntry()
    {
        var time = new ManualTimeProvider();
        await using var owner = SharpLinkAdmissionController.CreateDisabled(time);
        var kernel = owner.Kernel;
        var source = kernel.CreateProgram(
            PartitionOptions(ConnectionSelector, maxPartitions: 2, idleTimeout: TimeSpan.FromMinutes(1)), []);
        try
        {
            var first = await source.Controller.AcquireAsync(
                Context("tenant-a"), 1, false, CancellationToken.None);
            Ensure(first.IsAcquired, "tenant-a must create the first entry");
            first.Lease!.Dispose();
            time.Advance(TimeSpan.FromMinutes(2));

            var pool = source.Controller.PartitionStateForTests!;
            using var resolved = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var claimed = 0;
            pool.AfterKeyResolvedBeforeEntryLockForTests = () =>
            {
                if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0)
                    return;
                resolved.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("idle-reclaim acquire barrier timed out");
            };

            try
            {
                var reacquire = Task.Run(async () => await source.Controller.AcquireAsync(
                    Context("tenant-a"), 1, false, CancellationToken.None));
                Ensure(resolved.Wait(TimeSpan.FromSeconds(5)),
                    "tenant-a reacquire must pause before the namespace gate");

                var other = await source.Controller.AcquireAsync(
                    Context("tenant-b"), 1, false, CancellationToken.None);
                Ensure(other.IsAcquired && pool.Count == 1,
                    "tenant-b creation must reclaim the expired idle tenant-a entry first");
                other.Lease!.Dispose();

                resume.Set();
                var recreated = await reacquire.WaitAsync(TimeSpan.FromSeconds(5));
                Ensure(recreated.IsAcquired && pool.Count == 2,
                    "paused tenant-a acquire must safely create a replacement rather than use disposed entry state");
                recreated.Lease!.Dispose();
            }
            finally
            {
                pool.AfterKeyResolvedBeforeEntryLockForTests = null;
                resume.Set();
            }
        }
        finally
        {
            source.Retire();
        }
    }

    [Test]
    public async Task ConcurrentDuplicateKeyCreatorsShouldPublishExactlyOneEntry()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConstantSelectorA, maxPartitions: 1, permitLimit: 64));
        var current = Current(server);
        var pool = current.Controller.PartitionStateForTests!;

        var tasks = Enumerable.Range(0, 32).Select(async _ =>
            await current.Controller.AcquireAsync(
                Context("ignored"), 1, false, CancellationToken.None)).ToArray();
        var decisions = await Task.WhenAll(tasks);
        Ensure(decisions.All(static decision => decision.IsAcquired) && pool.Count == 1,
            "duplicate-key creators must linearize to one dictionary entry and one capacity charge");
        foreach (var decision in decisions)
            decision.Lease!.Dispose();
    }

    [Test]
    public async Task DisableReenableCompatibleNamespaceShouldPreserveConcurrencyPool()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            ConfigureQueue(options, TimeSpan.FromMinutes(1));
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 4, permitLimit: 1);
        });
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "old program must remain alive across ordinary Disable");
        var pool = source.Controller.PartitionStateForTests!;
        var context = Context("tenant-a");
        var holder = await source.Controller.AcquireAsync(context, 1, true, CancellationToken.None);
        var queued = source.Controller.AcquireAsync(context, 1, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => source.Kernel.QueuedCalls == 1,
            "old partition waiter must be resident before Disable");

        publicServer.DisableAdmissionControl();
        Ensure(!queued.IsCompleted,
            "ordinary Disable must not cancel an old captured partition waiter");
        publicServer.EnableAdmissionControl(options =>
        {
            ConfigureQueue(options, TimeSpan.FromMinutes(1));
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 4, permitLimit: 1);
        });
        var reenabled = Current(server);
        Ensure(ReferenceEquals(pool, reenabled.Controller.PartitionStateForTests),
            "compatible re-enable must reuse the latest live namespace instead of splitting capacity");
        var newAttempt = await reenabled.Controller.AcquireAsync(
            context, 1, false, CancellationToken.None);
        Ensure(!newAttempt.IsAcquired && newAttempt.Reason == "concurrency",
            "old holder must still consume the shared partition concurrency bound after re-enable");

        holder.Lease!.Dispose();
        var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(admitted.IsAcquired, "old waiter must complete normally after ordinary Disable/re-enable");
        admitted.Lease!.Dispose();
        source.ReleaseUse();
    }

    [Test]
    [NotInParallel]
    public async Task ConcurrentWorkersAndPartitionWriterShouldDrainToBoundedState()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureStressPolicy(
            options,
            ConnectionSelector,
            maxPartitions: 8,
            concurrency: 2,
            PartitionRateKind.TokenBucket,
            rateLimit: 100_000,
            idleTimeout: TimeSpan.FromHours(1)));
        var kernel = Current(server).Kernel;
        var errors = new ConcurrentQueue<Exception>();
        var stopWorkers = 0;

        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            var iteration = 0;
            while (Volatile.Read(ref stopWorkers) == 0)
            {
                AdmissionProgram? program = null;
                try
                {
                    program = server.CaptureAdmissionProgramForTests((worker + 1L) * 1_000_000 + iteration);
                    if (program is null)
                    {
                        await Task.Yield();
                        iteration++;
                        continue;
                    }

                    var decision = await program.Controller.AcquireAsync(
                        Context($"tenant-{worker & 1}"),
                        retainedBytes: 8,
                        allowQueue: true,
                        CancellationToken.None);
                    decision.Lease?.Dispose();
                }
                catch (Exception exception)
                {
                    errors.Enqueue(exception);
                }
                finally
                {
                    program?.ReleaseUse();
                }
                iteration++;
            }
        })).ToArray();

        var enabled = true;
        try
        {
            for (var iteration = 0; iteration < 24; iteration++)
            {
                var phase = iteration % 8;
                if (phase == 6)
                {
                    publicServer.DisableAdmissionControl();
                    enabled = false;
                    continue;
                }
                if (phase == 7)
                {
                    publicServer.EnableAdmissionControl(options => ConfigureStressPolicy(
                        options,
                        ConnectionSelector,
                        maxPartitions: 8,
                        concurrency: 2,
                        PartitionRateKind.TokenBucket,
                        rateLimit: 100_000,
                        idleTimeout: TimeSpan.FromHours(1)));
                    enabled = true;
                    continue;
                }

                var selector = phase == 5 ? ReplacementSelector : ConnectionSelector;
                var maxPartitions = phase == 1 ? 2 : 8;
                var concurrency = phase == 2 ? 1 : 2;
                var rateKind = phase switch
                {
                    3 => PartitionRateKind.FixedWindow,
                    4 => PartitionRateKind.SlidingWindow,
                    _ => PartitionRateKind.TokenBucket
                };
                var idleTimeout = phase == 4
                    ? TimeSpan.FromMilliseconds(1)
                    : TimeSpan.FromHours(1);
                publicServer.UpdateAdmissionControl(options => ConfigureStressPolicy(
                    options,
                    selector,
                    maxPartitions,
                    concurrency,
                    rateKind,
                    rateLimit: phase == 3 ? 50_000 : 100_000,
                    idleTimeout));
            }

            if (!enabled)
            {
                publicServer.EnableAdmissionControl(options => ConfigureStressPolicy(
                    options,
                    ConnectionSelector,
                    maxPartitions: 8,
                    concurrency: 2,
                    PartitionRateKind.TokenBucket,
                    rateLimit: 100_000,
                    idleTimeout: TimeSpan.FromHours(1)));
            }
        }
        finally
        {
            Volatile.Write(ref stopWorkers, 1);
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
        }

        await WaitUntilAsync(
            () => kernel.QueuedCalls == 0 && kernel.ActivePermits == 0 && kernel.RetiredProgramCount == 0,
            "stress workers must drain queue/permits and retired programs");
        var current = Current(server);
        var pool = current.Controller.PartitionStateForTests!;
        Ensure(errors.IsEmpty,
            $"stress must not surface ObjectDisposed/deadlock/accounting failures; first={errors.FirstOrDefault()}");
        Ensure(kernel.LiveProgramCount == 1 && kernel.PartitionStateCount == 1 &&
               kernel.PartitionEntryCount <= pool.MaxPartitionsForTests &&
               kernel.PartitionRuntimeGenerationCount == kernel.PartitionEntryCount &&
               kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
            "after concurrent update/selector/Disable traffic drains, partition generations and entries must converge to bounded steady state");
    }

    private static SharpLinkServer CreateServer()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        return (SharpLinkServer)builder.Build();
    }

    private static AdmissionProgram Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests ??
            throw new Exception("assert failed: expected enabled admission publication");

    private static SharpLinkAdmissionContext Context(string connectionId)
        => new(101, 202, RpcMethodKind.Unary, connectionId, null, null);

    private static void ConfigurePartitionConcurrency(
        SharpLinkAdmissionControlOptions options,
        Func<SharpLinkAdmissionContext, string?> selector,
        int maxPartitions,
        int permitLimit)
    {
        options.UsePartition(selector, partition =>
        {
            partition.MaxPartitions = maxPartitions;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseConcurrency(permitLimit);
        });
    }

    private static void ConfigurePartitionRate(
        SharpLinkAdmissionControlOptions options,
        Func<SharpLinkAdmissionContext, string?> selector,
        PartitionRateKind kind,
        int permitLimit)
    {
        options.UsePartition(selector, partition =>
        {
            partition.MaxPartitions = 8;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            ConfigureRate(partition, kind, permitLimit);
        });
    }

    private static void ConfigureStressPolicy(
        SharpLinkAdmissionControlOptions options,
        Func<SharpLinkAdmissionContext, string?> selector,
        int maxPartitions,
        int concurrency,
        PartitionRateKind rateKind,
        int rateLimit,
        TimeSpan idleTimeout)
    {
        ConfigureQueue(options, TimeSpan.FromMilliseconds(100));
        options.UsePartition(selector, partition =>
        {
            partition.MaxPartitions = maxPartitions;
            partition.IdleTimeout = idleTimeout;
            partition.UseConcurrency(concurrency);
            ConfigureRate(partition, rateKind, rateLimit);
        });
    }

    private static void ConfigureRate(
        SharpLinkAdmissionRuleOptions rule,
        PartitionRateKind kind,
        int permitLimit)
    {
        switch (kind)
        {
            case PartitionRateKind.TokenBucket:
                rule.UseTokenBucket(rate =>
                {
                    rate.TokenLimit = permitLimit;
                    rate.TokensPerPeriod = permitLimit;
                    rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
                });
                break;
            case PartitionRateKind.FixedWindow:
                rule.UseFixedWindow(rate =>
                {
                    rate.PermitLimit = permitLimit;
                    rate.Window = TimeSpan.FromHours(1);
                });
                break;
            case PartitionRateKind.SlidingWindow:
                rule.UseSlidingWindow(rate =>
                {
                    rate.PermitLimit = permitLimit;
                    rate.Window = TimeSpan.FromHours(1);
                    rate.SegmentsPerWindow = 4;
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static SharpLinkAdmissionControlOptions PartitionOptions(
        Func<SharpLinkAdmissionContext, string?> selector,
        int maxPartitions,
        TimeSpan idleTimeout)
    {
        var options = new SharpLinkAdmissionControlOptions();
        options.UsePartition(selector, partition =>
        {
            partition.MaxPartitions = maxPartitions;
            partition.IdleTimeout = idleTimeout;
            partition.UseConcurrency(1);
        });
        return options;
    }

    private static void ConfigureQueue(
        SharpLinkAdmissionControlOptions options,
        TimeSpan maxQueueDelay)
    {
        options.MaxQueuedCalls = 32;
        options.MaxQueuedBytes = 32 * 1024;
        options.MaxQueueDelay = maxQueueDelay;
    }

    private static Exception? CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Exception($"assert failed: {scenario}");
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    public enum PartitionRateKind
    {
        TokenBucket,
        FixedWindow,
        SlidingWindow
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow()
            => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());

        internal void Advance(TimeSpan elapsed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
            Interlocked.Add(ref _timestamp, elapsed.Ticks);
        }
    }
}
