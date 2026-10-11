#!/usr/bin/env python3
"""Issue #801: experiment-only removal of ServerDecodeExecutor's test queue.

This is run on an ephemeral GitHub Actions checkout. Tests formerly using
compatibility-only EnqueueAsync overloads are migrated to the exact
TryReserveQueueSlot + EnqueueReservedAsync path used by production.
No modified src/ or test/ content is pushed back to GitHub.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "artifacts/issue-801-decode-queue"
OUT.mkdir(parents=True, exist_ok=True)


def replace_exact(source: str, old: str, new: str, where: str, expected: int = 1) -> str:
    count = source.count(old)
    if count != expected:
        raise ValueError(f"{where}: expected {expected} pattern(s), found {count}: {old[:95]!r}")
    return source.replace(old, new)


def replace_region(source: str, start: str, end: str, replacement: str, where: str) -> str:
    if source.count(start) != 1 or source.count(end) != 1:
        raise ValueError(f"{where}: expected exactly one region delimiter")
    i = source.index(start)
    j = source.index(end)
    if j <= i:
        raise ValueError(f"{where}: region delimiters inverted")
    return source[:i] + replacement + source[j:]


def modify(relative: str, fn) -> None:
    file = ROOT / relative
    original = file.read_text(encoding="utf-8")
    adjusted = fn(original)
    adjusted = "\n".join(line.rstrip() for line in adjusted.split("\n"))
    if adjusted == original:
        raise RuntimeError(f"{relative}: no changes applied")
    file.write_text(adjusted, encoding="utf-8", newline="\n")
    changed.append(relative)


changed: list[str] = []
source_path = "src/SharpLink.Server/ServerDecodeExecutor.cs"


def strip_compat_executor(s: str) -> str:
    # Remove static compatibility key and always-allocated compatibility state.
    for statement in (
        "    private static readonly object s_compatibilitySchedulingKey = new();\n\n",
        "    private readonly SemaphoreSlim _compatibilitySlots;\n",
        "    private readonly CancellationTokenSource _compatibilityStop = new();\n",
        "    private readonly TaskCompletionSource _compatibilityOperationsDrained =\n"
        "        new(TaskCreationOptions.RunContinuationsAsynchronously);\n",
        "    private int _compatibilityOperations;\n",
        "        _compatibilitySlots = new SemaphoreSlim(queueCapacity, queueCapacity);\n",
    ):
        s = replace_exact(s, statement, "", "compatibility field/constructor")

    s = replace_exact(
        s,
        "    /// Number of published operations waiting for worker service, plus compatibility-path writers\n"
        "    /// blocked by queue capacity. Production reserved publication never blocks on scheduler capacity.",
        "    /// Number of published operations waiting for worker service. Reserved publication\n"
        "    /// never blocks while retaining decoded request resources.",
        "QueueDepth documentation",
    )

    s = replace_exact(
        s,
        "            queuePermit,\n            releaseCompatibilitySlot: false);",
        "            queuePermit);",
        "sole production entry constructor",
    )

    s = replace_region(
        s,
        "    /// <summary>\n    /// Compatibility/test publication path retained",
        "    internal void StopAccepting()",
        "",
        "delete both compatibility EnqueueAsync overloads",
    )

    s = replace_region(
        s,
        "    internal void StopAccepting()",
        "    internal void ReleaseQueueReservation()",
        """    internal void StopAccepting()
    {
        lock (_schedulerGate)
        {
            if (Volatile.Read(ref _completionRequested) != 0)
                return;

            Volatile.Write(ref _completionRequested, 1);
        }

        _readySignal.Release(_workers.Length);
    }

    internal async ValueTask CompleteAsync()
    {
        StopAccepting();
        await _completion.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;

        _readySignal.Dispose();
    }

""",
        "stop/complete/dispose without compatibility lifecycle",
    )

    s = replace_region(
        s,
        "    private async ValueTask EnqueueTrackedCompatibilityAsync(",
        "    private async Task WorkerLoopAsync()",
        "",
        "delete compatibility asynchronous queue and slot blocking path",
    )

    s = replace_exact(
        s,
        "                // Stop seals publication and cancels compatibility writers that have not published.\n"
        "                // With no ready connection left there is therefore no work a worker can still own,\n"
        "                // even if QueueDepth transiently includes a blocked writer rolling back its count.",
        "                // Stop has sealed publication; no ready connection means no worker-owned work remains.",
        "worker exit comments",
    )

    s = replace_exact(
        s,
        "    private void PublishEntryLocked(\n"
        "        ServerDecodeQueueEntry entry,\n"
        "        bool queueDepthAlreadyOwned = false)",
        "    private void PublishEntryLocked(ServerDecodeQueueEntry entry)",
        "publish signature",
    )
    s = replace_exact(
        s,
        "        if (!queueDepthAlreadyOwned)\n"
        "            Interlocked.Increment(ref _queueDepth);",
        "        Interlocked.Increment(ref _queueDepth);",
        "queue depth always published by reserved path",
    )

    s = replace_region(
        s,
        "    private void ReleaseQueuedOwnership(ServerDecodeQueueEntry entry)",
        "    private void DecrementQueueDepth()",
        """    private static void ReleaseQueuedOwnership(ServerDecodeQueueEntry entry)
        => entry.QueuePermit.Dispose();

""",
        "remove compatibility completion accounting and release",
    )

    s = replace_exact(
        s,
        "        ServerDecodeWorkItem workItem,\n"
        "        ServerDecodeQueuePermit? queuePermit,\n"
        "        bool releaseCompatibilitySlot)",
        "        ServerDecodeWorkItem workItem,\n"
        "        ServerDecodeQueuePermit queuePermit)",
        "queue entry constructor",
    )

    s = replace_exact(
        s,
        "        internal ServerDecodeQueuePermit? QueuePermit { get; } = queuePermit;\n\n"
        "        internal bool ReleaseCompatibilitySlot { get; } = releaseCompatibilitySlot;",
        "        internal ServerDecodeQueuePermit QueuePermit { get; } = queuePermit;",
        "queue entry ownership fields",
    )

    for obsolete in (
        "s_compatibilitySchedulingKey", "_compatibilitySlots", "_compatibilityStop",
        "_compatibilityOperationsDrained", "_compatibilityOperations",
        "EnqueueCoreAsync", "EnqueueTrackedCompatibilityAsync",
        "ReleaseCompatibilitySlot", "queueDepthAlreadyOwned"
    ):
        if obsolete in s:
            raise AssertionError(f"production code retains obsolete compatibility member: {obsolete}")

    return s


modify(source_path, strip_compat_executor)

helper = """    // Tests now publish through the exact, globally bounded production queue API.
    private static ValueTask EnqueueProduction(
        ServerDecodeExecutor executor,
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
        => EnqueueProduction(executor, executor, workItem, cancellationToken);

    private static ValueTask EnqueueProduction(
        ServerDecodeExecutor executor,
        object schedulingKey,
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (!executor.TryReserveQueueSlot(out var queuePermit) || queuePermit is null)
            return cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled(cancellationToken)
                : ValueTask.FromException(new ServerDecodeExecutorClosedException());

        return executor.EnqueueReservedAsync(
            schedulingKey, queuePermit, workItem, cancellationToken);
    }

"""

blocked_new = """    [Test]
    public async Task ProductionQueueShouldRejectOverCapacityWithoutBlockingPublication()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        var started = NewSignal();
        var release = NewSignal();
        var first = EnqueueProduction(executor,
            new ServerDecodeWorkItem(async _ =>
            {
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }), CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = EnqueueProduction(executor,
            new ServerDecodeWorkItem(_ => ValueTask.CompletedTask),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() => executor.QueueReservations == 1
            && executor.QueueDepth == 1, "second work must own the one queue reservation");

        Ensure(!executor.TryReserveQueueSlot(out var rejected) && rejected is null,
            "a third queued production request must immediately fail global admission");
        Ensure(executor.QueueReservations == 1 && executor.QueueDepth == 1,
            "capacity rejection cannot modify the already accepted pending work");
        Ensure(executor.SkippedBeforeStart == 0,
            "a request never admitted to the scheduler cannot be counted as skipped");

        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(executor.QueueReservations == 0 && executor.QueueDepth == 0,
            "worker dequeue and completion must release all reserved queue ownership");
        Ensure(executor.ScheduledConnectionCount == 0,
            "the production fair scheduler must reclaim its empty metadata");
    }

"""

stop_new = """    [Test]
    public async Task StopAcceptingShouldRejectNewReservationsAndDrainPublishedWork()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        var started = NewSignal();
        var release = NewSignal();
        var executedSecond = 0;

        var first = EnqueueProduction(executor,
            new ServerDecodeWorkItem(async _ =>
            {
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }), CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = EnqueueProduction(executor,
            new ServerDecodeWorkItem(_ =>
            {
                Interlocked.Increment(ref executedSecond);
                return ValueTask.CompletedTask;
            }), CancellationToken.None).AsTask();
        await WaitUntilAsync(() => executor.QueueReservations == 1
            && executor.QueueDepth == 1, "second request must be published before stop");

        executor.StopAccepting();
        Ensure(!executor.IsAccepting, "StopAccepting must seal publication synchronously");
        Ensure(!executor.TryReserveQueueSlot(out var rejectedPermit) && rejectedPermit is null,
            "post-stop request must not acquire queue admission");

        var rejected = EnqueueProduction(executor,
            new ServerDecodeWorkItem(_ => ValueTask.CompletedTask),
            CancellationToken.None).AsTask();
        await EnsureFailsAsync<ServerDecodeExecutorClosedException>(
            rejected, "post-stop production admission");
        Ensure(executor.QueueDepth == 1 && executor.QueueReservations == 1,
            "rejected requests cannot consume the already published work reservation");

        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(executedSecond == 1, "published work must drain and execute exactly once");
        Ensure(executor.QueueReservations == 0 && executor.QueueDepth == 0,
            "drained executor must release all production queue reservations");
        Ensure(executor.ScheduledConnectionCount == 0,
            "drain must release all fair scheduling metadata");
    }

"""


def migrate_main_tests(s: str) -> str:
    s = replace_exact(s, "executor.EnqueueAsync(", "EnqueueProduction(executor, ",
                      "test-only enqueue API", expected=23)
    s = replace_region(
        s, "    [Test]\n    public async Task BlockedWriterCancellationShouldRollbackPendingDepthWithoutPublication()",
        "    [Test]\n    public async Task WorkerWinningCancellationRaceShouldKeepCallerJoinedUntilProviderReturns()",
        blocked_new,
        "blocked compatibility enqueue test -> bounded global reservation test",
    )
    s = replace_region(
        s, "    [Test]\n    public async Task StopAcceptingShouldRejectBlockedWriterAndDrainPublishedWork()",
        "    [Test]\n    public async Task CompleteShouldStopPublicationAndDrainAlreadyPublishedWork()",
        stop_new,
        "stop compatibility test -> real reserved publication test",
    )
    s = replace_exact(
        s,
        "    private static ServerDecodeWorkItem NewRecordingWorkItem(",
        helper + "    private static ServerDecodeWorkItem NewRecordingWorkItem(",
        "production test helper",
    )
    if "EnqueueAsync(" in s or "blocked writer" in s.lower():
        raise AssertionError("outdated compatibility testing language/calls remain")
    return s


modify("test/SharpLink.UnitTests/Server/ServerDecodeExecutorTests.cs", migrate_main_tests)

scheduler_test = """    [Test]
    public async Task ReservedPublicationCrossingStopMustRejectAndReleaseReservation()
    {
        await using var executor = new ServerDecodeExecutor(workerCount: 1, queueCapacity: 1);
        Ensure(executor.TryReserveQueueSlot(out var permit) && permit is not null,
            "production publisher must reserve before retaining request payload");
        Ensure(executor.QueueReservations == 1 && executor.QueueDepth == 0,
            "the request owns one reservation but has not yet published a queue entry");

        executor.StopAccepting();
        Ensure(!executor.IsAccepting, "stop must close queue admission");
        Ensure(!executor.TryReserveQueueSlot(out var rejected) && rejected is null,
            "later work cannot enter the scheduler after the stop boundary");

        var attempts = 0;
        var unpublished = executor.EnqueueReservedAsync(new object(), permit!,
            new ServerDecodeWorkItem(_ =>
            {
                Interlocked.Increment(ref attempts);
                return ValueTask.CompletedTask;
            }), CancellationToken.None).AsTask();
        await EnsureFailsAsync<ServerDecodeExecutorClosedException>(
            unpublished, "reserved publisher crossing the stop boundary");

        Ensure(attempts == 0, "unpublished provider work must never run");
        Ensure(executor.QueueReservations == 0 && executor.QueueDepth == 0,
            "failed publication must release the previously reserved queue capacity");
        await executor.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

"""
scheduler_helper = """    private static ValueTask EnqueueProduction(
        ServerDecodeExecutor executor,
        object schedulingKey,
        ServerDecodeWorkItem item,
        CancellationToken cancellationToken)
    {
        if (!executor.TryReserveQueueSlot(out var permit) || permit is null)
            return cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled(cancellationToken)
                : ValueTask.FromException(new ServerDecodeExecutorClosedException());
        return executor.EnqueueReservedAsync(schedulingKey, permit, item, cancellationToken);
    }

"""


def migrate_race_tests(s: str) -> str:
    s = replace_exact(
        s, "executor.EnqueueAsync(",
        "EnqueueProduction(executor, ",
        "fair scheduling race tests using compatibility keyed enqueue",
        expected=3,
    )
    s = replace_region(
        s,
        "    [Test]\n    public async Task DisposeShouldWaitForCompatibilityWriterThatAcquiredSlotBeforePublication()",
        "    private static TaskCompletionSource NewSignal()",
        scheduler_test + scheduler_helper,
        "drop compatibility-slot reflection case and replace with pre-publication stop race",
    )
    if "ReadPrivateField" in s or "_compatibilitySlots" in s or "EnqueueAsync(" in s:
        raise AssertionError("legacy compatibility test references remain")
    return s


modify(
    "test/SharpLink.UnitTests/Server/ServerDecodeExecutorSchedulerRaceTests.cs",
    migrate_race_tests,
)

result = {
    "mode": "runner-only compatibility queue branch removal with test migration",
    "sourceBaseline": "dev:eb99fe887cf2129d9b88441245ca0a4a6406b6c2",
    "changedFiles": changed,
    "productionRemoved": [
        "ServerDecodeExecutor.EnqueueAsync(workItem, token)",
        "ServerDecodeExecutor.EnqueueAsync(key, workItem, token)",
        "EnqueueTrackedCompatibilityAsync",
        "EnqueueCoreAsync",
        "_compatibilitySlots SemaphoreSlim",
        "_compatibilityStop CancellationTokenSource",
        "_compatibilityOperationsDrained TaskCompletionSource",
        "_compatibilityOperations accounting",
        "ReleaseCompatibilitySlot entry flag/branch",
    ],
    "testMigrations": [
        "All eight executor-local tests now use production queue reservation and publication",
        "Scheduler fairness and queued cancellation race uses production queue reservation and keyed publication",
        "Blocked compatibility writer tests replaced with production capacity rejection and reserved stop crossover tests",
    ],
    "testCoverageCaution": "Old compatibility-only blocked-writer behavior is deliberately deleted, not preserved. Production reserved-before-retention semantics tested instead.",
}
(OUT / "decode-compatibility-removal.json").write_text(
    json.dumps(result, indent=2) + "\n", encoding="utf-8"
)
print(json.dumps(result, indent=2))
