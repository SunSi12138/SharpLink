#!/usr/bin/env bash
set -euo pipefail

BRANCH="agent/issue-252-pending-segments"
git fetch --depth=1 origin dev "$BRANCH"
git checkout -B "$BRANCH" "origin/$BRANCH"

git show origin/dev:src/SharpLink.Client/PendingRequestTable.cs > src/SharpLink.Client/PendingRequestTable.cs

python3 <<'PY'
from pathlib import Path
p=Path('src/SharpLink.Client/PendingRequestTable.cs')
t=p.read_text()

def one(old,new):
    global t
    n=t.count(old)
    if n!=1: raise SystemExit(f'expected 1 match, got {n}: {old[:100]!r}')
    t=t.replace(old,new,1)

def many(old,new,n):
    global t
    got=t.count(old)
    if got!=n: raise SystemExit(f'expected {n} matches, got {got}: {old[:100]!r}')
    t=t.replace(old,new)

one(
"    private readonly int _indexMask;\n    private readonly PendingCall?[] _slots;\n",
"    private const int DeadlinePageShift = 8;\n    private const int DeadlinePageSize = 1 << DeadlinePageShift;\n    private readonly int _indexMask;\n    private readonly int _capacity;\n    private readonly object _slotsInitializationGate = new();\n    private readonly int[] _deadlinePageCounts;\n    private PendingCall?[]? _slots;\n")
one(
"        _slots = new PendingCall?[capacity];\n        _indexMask = capacity - 1;\n",
"        _capacity = capacity;\n        _deadlinePageCounts = new int[(capacity + DeadlinePageSize - 1) >> DeadlinePageShift];\n        _indexMask = capacity - 1;\n")
one("    public int Capacity => _slots.Length;\n", "    public int Capacity => _capacity;\n")
one(
"    internal int ActiveCount => Volatile.Read(ref _activeSlots);\n",
"    internal int ActiveCount => Volatile.Read(ref _activeSlots);\n\n    internal bool SlotsMaterialized => Volatile.Read(ref _slots) is not null;\n\n    internal int LastDeadlineScanInspectedSlots { get; private set; }\n")
one(
"        {\n            var count = 0;\n            for (var index = 0; index < _slots.Length; index++)\n                if (Volatile.Read(ref _slots[index]) is not null)\n                    count++;\n            return count;\n        }\n",
"        {\n            var slots = Volatile.Read(ref _slots);\n            if (slots is null)\n                return 0;\n\n            var count = 0;\n            for (var index = 0; index < slots.Length; index++)\n                if (Volatile.Read(ref slots[index]) is not null)\n                    count++;\n            return count;\n        }\n")
one(
"    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)\n    {\n        var index = (int)(id & _indexMask);\n        var current = Volatile.Read(ref _slots[index]);\n",
"    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)\n    {\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n            return false;\n\n        var index = (int)(id & _indexMask);\n        var current = Volatile.Read(ref slots[index]);\n")
one(
"                if (!ReferenceEquals(Volatile.Read(ref _slots[index]), current) ||\n                    current.Id != id ||\n",
"                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) ||\n                    current.Id != id ||\n")
one(
"    public bool Contains(long id)\n    {\n        var call = Volatile.Read(ref _slots[(int)(id & _indexMask)]);\n        return call is not null && call.Id == id;\n    }\n",
"    public bool Contains(long id)\n    {\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n            return false;\n\n        var call = Volatile.Read(ref slots[(int)(id & _indexMask)]);\n        return call is not null && call.Id == id;\n    }\n")
one(
"    public CancellationToken GetProducerCancellationToken(long id)\n    {\n        var call = Volatile.Read(ref _slots[(int)(id & _indexMask)]);\n        if (call is null || call.Id != id)\n            return new CancellationToken(canceled: true);\n        return call.ProducerCancellationToken;\n    }\n",
"    public CancellationToken GetProducerCancellationToken(long id)\n    {\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n            return new CancellationToken(canceled: true);\n\n        var call = Volatile.Read(ref slots[(int)(id & _indexMask)]);\n        if (call is null || call.Id != id)\n            return new CancellationToken(canceled: true);\n        return call.ProducerCancellationToken;\n    }\n")
one(
"    public void FailAllPendingRequests(Exception exception)\n    {\n        ArgumentNullException.ThrowIfNull(exception);\n        for (var index = 0; index < _slots.Length; index++)\n",
"    public void FailAllPendingRequests(Exception exception)\n    {\n        ArgumentNullException.ThrowIfNull(exception);\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n            return;\n\n        for (var index = 0; index < slots.Length; index++)\n")
many(
"        var published = false;\n        try\n        {\n            while (true)\n            {\n                for (var attempt = 0; attempt < _slots.Length; attempt++)\n",
"        var published = false;\n        try\n        {\n            var slots = GetOrCreateSlots();\n            while (true)\n            {\n                for (var attempt = 0; attempt < slots.Length; attempt++)\n",2)
many("                    if (Volatile.Read(ref _slots[index]) is not null)\n", "                    if (Volatile.Read(ref slots[index]) is not null)\n",2)
many(
"                    if (Interlocked.CompareExchange(ref _slots[index], call, null) is null)\n",
"                    if (deadline.HasValue)\n                        Interlocked.Increment(ref _deadlinePageCounts[index >> DeadlinePageShift]);\n                    if (Interlocked.CompareExchange(ref slots[index], call, null) is null)\n",2)
many(
"\n                    call.ReturnUnused();\n",
"\n                    if (deadline.HasValue)\n                        Interlocked.Decrement(ref _deadlinePageCounts[index >> DeadlinePageShift]);\n                    call.ReturnUnused();\n",2)
one("        if (active <= _slots.Length)\n", "        if (active <= _capacity)\n")
one("        if (remaining < _slots.Length && Volatile.Read(ref _waiterCount) != 0)\n", "        if (remaining < _capacity && Volatile.Read(ref _waiterCount) != 0)\n")
one(
"    private bool TryTakeMatchingCall(long id, out PendingCall? call)\n    {\n        var index = (int)(id & _indexMask);\n",
"    private bool TryTakeMatchingCall(long id, out PendingCall? call)\n    {\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n        {\n            call = null;\n            return false;\n        }\n\n        var index = (int)(id & _indexMask);\n")
one(
"                var exchanged = Interlocked.CompareExchange(ref _slots[index], null, current);\n                if (!ReferenceEquals(exchanged, current))\n                    continue;\n\n                current.WaitUntilRegistered();\n",
"                var exchanged = Interlocked.CompareExchange(ref slots[index], null, current);\n                if (!ReferenceEquals(exchanged, current))\n                    continue;\n\n                if (current.Deadline.HasValue)\n                    Interlocked.Decrement(ref _deadlinePageCounts[index >> DeadlinePageShift]);\n                current.WaitUntilRegistered();\n")
one(
"    private bool TryTakeCallAtIndex(int index, out PendingCall? call)\n    {\n        while (true)\n",
"    private bool TryTakeCallAtIndex(int index, out PendingCall? call)\n    {\n        var slots = Volatile.Read(ref _slots)!;\n        while (true)\n")
one(
"                if (!ReferenceEquals(Volatile.Read(ref _slots[index]), current))\n                    continue;\n\n                if (!ReferenceEquals(Interlocked.CompareExchange(ref _slots[index], null, current), current))\n                    continue;\n\n                current.WaitUntilRegistered();\n",
"                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current))\n                    continue;\n\n                if (!ReferenceEquals(Interlocked.CompareExchange(ref slots[index], null, current), current))\n                    continue;\n\n                if (current.Deadline.HasValue)\n                    Interlocked.Decrement(ref _deadlinePageCounts[index >> DeadlinePageShift]);\n                current.WaitUntilRegistered();\n")

marker="    private bool TryAcquireCapacity()\n"
helper="""    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PendingCall?[] GetOrCreateSlots()
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is not null)
            return slots;

        lock (_slotsInitializationGate)
        {
            slots = Volatile.Read(ref _slots);
            if (slots is null)
            {
                slots = new PendingCall?[_capacity];
                Volatile.Write(ref _slots, slots);
            }

            return slots;
        }
    }

"""
one(marker,helper+marker)
one(
"            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);\n            var now = _timeProvider.GetTimestamp();\n            for (var index = 0; index < _slots.Length; index++)\n            {\n                var call = Volatile.Read(ref _slots[index]);\n                if (call is null || !call.Deadline.HasValue)\n                    continue;\n                if (call.Deadline.Timestamp <= now)\n                    TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);\n                else\n                    UpdateEarliestDeadline(call.Deadline.Timestamp);\n            }\n",
"            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);\n            var slots = Volatile.Read(ref _slots);\n            if (slots is null)\n            {\n                LastDeadlineScanInspectedSlots = 0;\n                return;\n            }\n\n            var now = _timeProvider.GetTimestamp();\n            var inspectedSlots = 0;\n            for (var page = 0; page < _deadlinePageCounts.Length; page++)\n            {\n                if (Volatile.Read(ref _deadlinePageCounts[page]) == 0)\n                    continue;\n\n                var start = page << DeadlinePageShift;\n                var end = Math.Min(start + DeadlinePageSize, slots.Length);\n                for (var index = start; index < end; index++)\n                {\n                    inspectedSlots++;\n                    var call = Volatile.Read(ref slots[index]);\n                    if (call is null || !call.Deadline.HasValue)\n                        continue;\n                    if (call.Deadline.Timestamp <= now)\n                        TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);\n                    else\n                        UpdateEarliestDeadline(call.Deadline.Timestamp);\n                }\n            }\n\n            LastDeadlineScanInspectedSlots = inspectedSlots;\n")
many("ref _slots[index]", "ref slots[index]", 3)
if "_slots.Length" in t or "ref _slots[index]" in t:
    raise SystemExit('C2 production patch left direct nullable-slot accesses')
p.write_text(t)
PY

rm -f src/SharpLink.Client/SegmentedSlotTable.cs
rm -f test/SharpLink.UnitTests/Runtime/PendingRequestTableSegmentationTests.cs
cat > test/SharpLink.UnitTests/Runtime/PendingRequestTableStorageTests.cs <<'CS'
using System.Buffers;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class PendingRequestTableStorageTests
{
    [Test]
    public void IdleTableShouldNotMaterializeFlatSlots()
    {
        using var table = PendingRequestTableTestFixture.Create(65_536);
        Ensure(table.Capacity == 65_536, "logical capacity must remain unchanged");
        Ensure(!table.SlotsMaterialized, "idle construction must not allocate the full slot array");
        Ensure(table.Count == 0 && table.ActiveCount == 0, "idle counts must remain zero");
    }

    [Test]
    public void ReadOnlyLookupsShouldNotMaterializeFlatSlots()
    {
        using var table = PendingRequestTableTestFixture.Create(65_536);
        const long missingId = 700;
        Ensure(!table.Contains(missingId), "contains should reject a missing request");
        Ensure(!table.TryComplete(missingId, PendingCallCompletionReason.UserCancellation),
            "terminal lookup should reject a missing request");
        Ensure(table.GetProducerCancellationToken(missingId).IsCancellationRequested,
            "producer token lookup should preserve the missing-call contract");
        Ensure(!table.SlotsMaterialized, "read-only lookups must not allocate flat slots");
    }

    [Test]
    public async Task FirstRegistrationShouldMaterializeAndRetainFlatSlots()
    {
        using var table = PendingRequestTableTestFixture.Create(65_536);
        var operation = table.Rent<int>(out var id);
        Ensure(table.SlotsMaterialized, "first real registration must materialize flat slots");
        Ensure(table.Count == 1 && table.ActiveCount == 1, "registration must publish exactly once");
        await CompleteForCleanup(table, id, operation);
        Ensure(table.SlotsMaterialized, "flat slots are retained after first materialization");
        Ensure(table.Count == 0 && table.ActiveCount == 0, "completion must release capacity exactly once");
    }

    [Test]
    public async Task ConcurrentFirstUseShouldPublishAllCalls()
    {
        const int count = 32;
        using var table = PendingRequestTableTestFixture.Create(65_536);
        var ids = new long[count];
        var operations = new RpcRequestOperation<int>[count];
        Parallel.For(0, count, i => operations[i] = table.Rent<int>(out ids[i]));
        Ensure(table.SlotsMaterialized, "concurrent first use must converge on one published flat table");
        Ensure(table.Count == count && table.ActiveCount == count, "all reserved calls must publish");
        for (var i = 0; i < count; i++)
            await CompleteForCleanup(table, ids[i], operations[i]);
        Ensure(table.ActiveCount == 0, "concurrent cleanup must release every reservation");
    }

    [Test]
    public async Task SparseDeadlineScanShouldInspectOnePage()
    {
        var timeProvider = new ManualTimeProvider();
        using var table = PendingRequestTableTestFixture.Create(65_536, timeProvider: timeProvider);
        var deadline = RpcDeadline.Create(timeProvider.GetUtcNow().AddSeconds(1), timeProvider);
        var operation = table.Rent(new Int32Codec(), PendingCallKind.Unary, deadline,
            CancellationToken.None, out _);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var failure = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "deadline expiration must preserve the terminal result");
        Ensure(table.LastDeadlineScanInspectedSlots == 256,
            "one sparse deadline page should inspect 256 slots, not full capacity");
        Ensure(table.ActiveCount == 0, "deadline completion must release capacity");
    }

    [Test]
    public async Task CompletedDeadlinePageShouldNotRemainMarked()
    {
        var timeProvider = new ManualTimeProvider();
        using var table = PendingRequestTableTestFixture.Create(65_536, timeProvider: timeProvider);
        var farDeadline = RpcDeadline.Create(timeProvider.GetUtcNow().AddMinutes(5), timeProvider);
        var first = table.Rent(new Int32Codec(), PendingCallKind.Unary, farDeadline,
            CancellationToken.None, out var firstId);
        await CompleteForCleanup(table, firstId, first);

        for (var i = 0; i < 254; i++)
        {
            var operation = table.Rent<int>(out var id);
            await CompleteForCleanup(table, id, operation);
        }

        var expiring = RpcDeadline.Create(timeProvider.GetUtcNow().AddSeconds(1), timeProvider);
        var second = table.Rent(new Int32Codec(), PendingCallKind.Unary, expiring,
            CancellationToken.None, out var secondId);
        Ensure((secondId >> 8) != (firstId >> 8), "test must move the deadline call to the next page");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var failure = await CaptureExceptionAsync(second.AsValueTask().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "second-page deadline must expire normally");
        Ensure(table.LastDeadlineScanInspectedSlots == 256,
            "a completed deadline page must clear so only the currently active page is scanned");
    }

    private static async Task CompleteForCleanup(PendingRequestTable table, long id, RpcRequestOperation<int> operation)
    {
        var cleanup = new IOException("pending storage test cleanup");
        Ensure(table.TryComplete(id, PendingCallCompletionReason.ConnectionClosed, cleanup),
            "cleanup should win the pending slot exactly once");
        var failure = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
        Ensure(ReferenceEquals(failure, cleanup), "cleanup exception should flow through the operation");
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try { await task; return null; }
        catch (Exception exception) { return exception; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
CS

# Keep the reusable concurrency benchmark, but rename its segmented terminology for the flat candidate.
if [[ -f test/SharpLink.Benchmarks/PendingRequestCrossSegmentConcurrencyBenchmarks.cs ]]; then
  mv test/SharpLink.Benchmarks/PendingRequestCrossSegmentConcurrencyBenchmarks.cs test/SharpLink.Benchmarks/PendingRequestConcurrencyBenchmarks.cs
  python3 <<'PY'
from pathlib import Path
p=Path('test/SharpLink.Benchmarks/PendingRequestConcurrencyBenchmarks.cs')
t=p.read_text()
t=t.replace('PendingRequestCrossSegmentConcurrencyBenchmarks','PendingRequestConcurrencyBenchmarks')
t=t.replace('RegisterAndCompleteAcrossFourSegments','RegisterAndCompleteAcrossFourWindows')
t=t.replace('more than one segment in flight','1,024 requests in flight')
t=t.replace('different 256-slot segment','different 256-request window')
t=t.replace('cross-segment access pattern','multi-window concurrent access pattern')
t=t.replace('same-segment\n/// pattern','single-thread\n/// pattern')
t=t.replace('segment boundary','256-request boundary')
t=t.replace('old segment','old request window')
t=t.replace('older segments','older request windows')
p.write_text(t)
PY
fi

# Remove temporary evidence/landing machinery and restore the repository's real PR workflow.
rm -f .github/issue252-c2-evidence.sh .github/issue252-c2-rerun.sh .github/issue252-land-c2.sh
git show origin/dev:.github/workflows/pr-quick.yml > .github/workflows/pr-quick.yml

git diff --check
git status --short

git config user.name "github-actions[bot]"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
git add -- src/SharpLink.Client/PendingRequestTable.cs src/SharpLink.Client/SegmentedSlotTable.cs \
  test/SharpLink.UnitTests/Runtime/PendingRequestTableSegmentationTests.cs \
  test/SharpLink.UnitTests/Runtime/PendingRequestTableStorageTests.cs \
  test/SharpLink.Benchmarks/PendingRequestCrossSegmentConcurrencyBenchmarks.cs \
  test/SharpLink.Benchmarks/PendingRequestConcurrencyBenchmarks.cs \
  .github/issue252-c2-evidence.sh .github/issue252-c2-rerun.sh .github/issue252-land-c2.sh \
  .github/workflows/pr-quick.yml
if git diff --cached --quiet; then
  echo "C2 production landing already applied"
  exit 0
fi
git commit -m "perf(client): lazily materialize pending slots"
git push origin HEAD:"$BRANCH"
