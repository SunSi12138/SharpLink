#!/usr/bin/env bash
set -euo pipefail

ROOT="$GITHUB_WORKSPACE"
BRANCH="agent/issue-252-pending-segments"
BASE="$RUNNER_TEMP/issue252-deadline-base"
CANDIDATE="$RUNNER_TEMP/issue252-deadline-candidate"
OUT="$RUNNER_TEMP/issue252-deadline-results"

rm -rf "$BASE" "$CANDIDATE" "$OUT"
mkdir -p "$OUT"

git fetch --no-tags origin dev "$BRANCH"
git worktree add --detach "$BASE" origin/dev
git worktree add --detach "$CANDIDATE" "origin/$BRANCH"
trap 'git -C "$ROOT" worktree remove --force "$BASE" >/dev/null 2>&1 || true; git -C "$ROOT" worktree remove --force "$CANDIDATE" >/dev/null 2>&1 || true' EXIT

echo "[issue252-deadline] base_sha=$(git -C "$BASE" rev-parse HEAD)"
echo "[issue252-deadline] candidate_sha=$(git -C "$CANDIDATE" rev-parse HEAD)"

# Use the same deadline benchmark source on both worktrees.
cp "$ROOT/test/SharpLink.Benchmarks/PendingRequestDeadlineBenchmarks.cs" \
   "$BASE/test/SharpLink.Benchmarks/PendingRequestDeadlineBenchmarks.cs"

# Theory-selected candidate: compact shared bitmap plus a value-only ThreadStatic locality cache.
# A scanner advances a table epoch before consuming any bits. Registration publishes the slot first,
# then reads that epoch. A cache hit therefore means either (a) the page bit is still live and a future
# scanner will consume it, or (b) a concurrent scanner's epoch transition linearizes after this read,
# in which case that scanner still consumes the old live bit and scans the already-published slot.
# Reading the new epoch invalidates the TLS hit and forces republishing. TLS stores only numeric table id,
# page and epoch, so it cannot retain a PendingRequestTable on a long-lived worker thread.
python3 - "$CANDIDATE/src/SharpLink.Client/PendingRequestTable.cs" \
          "$BASE/test/SharpLink.Benchmarks/PendingRequestDeadlineBenchmarks.cs" \
          "$CANDIDATE/test/SharpLink.Benchmarks/PendingRequestDeadlineBenchmarks.cs" <<'PY'
from pathlib import Path
import sys

source_path = Path(sys.argv[1])
benchmark_paths = [Path(sys.argv[2]), Path(sys.argv[3])]


def replace_exact(text, old, new, expected=1, label="replacement"):
    count = text.count(old)
    if count != expected:
        raise SystemExit(f"{label}: expected {expected} matches, found {count}")
    return text.replace(old, new)

source = source_path.read_text(encoding="utf-8")

source = replace_exact(
    source,
    '''    private const int DeadlinePagesPerWord = 1 << DeadlinePagesPerWordShift;
    private const int DeadlineRegistrationStripeCount = 16;
    private const int DeadlineRegistrationStripeMask = DeadlineRegistrationStripeCount - 1;
    private const int DeadlineRetentionStripe = DeadlineRegistrationStripeCount;
    private const int DeadlineMarkerStripeCount = DeadlineRegistrationStripeCount + 1;
    private const int DeadlineMarkerCacheLineLongs = 8;
''',
    '''    private const int DeadlinePagesPerWord = 1 << DeadlinePagesPerWordShift;
    private static long s_nextDeadlineMarkerCacheId;
    [ThreadStatic] private static long t_deadlineMarkerCacheId;
    [ThreadStatic] private static long t_deadlineMarkerCacheEpoch;
    [ThreadStatic] private static int t_deadlineMarkerCachePage;
''',
    label="replace stripe constants with TLS cache fields")

source = replace_exact(
    source,
    '''    private readonly int _deadlinePageWordCount;
    private readonly int _deadlineMarkerStripeStride;
    private readonly long[] _deadlinePageBits;
''',
    '''    private readonly int _deadlinePageWordCount;
    private readonly long[] _deadlinePageBits;
    private readonly long _deadlineMarkerCacheId;
''',
    label="replace stripe storage fields")

source = replace_exact(
    source,
    '''    private long _approximateEarliestDeadline = long.MaxValue;
    private int _deadlineScanRunning;
''',
    '''    private long _approximateEarliestDeadline = long.MaxValue;
    private long _deadlineMarkerEpoch;
    private int _deadlineScanRunning;
''',
    label="add scanner epoch")

source = replace_exact(
    source,
    '''        _deadlinePageWordCount = (deadlinePageCount + DeadlinePagesPerWord - 1) >> DeadlinePagesPerWordShift;
        var stripeLongs = 1 + _deadlinePageWordCount;
        _deadlineMarkerStripeStride =
            (stripeLongs + DeadlineMarkerCacheLineLongs - 1) & ~(DeadlineMarkerCacheLineLongs - 1);
        _deadlinePageBits = new long[_deadlineMarkerStripeStride * DeadlineMarkerStripeCount];
        _indexMask = capacity - 1;
''',
    '''        _deadlinePageWordCount = (deadlinePageCount + DeadlinePagesPerWord - 1) >> DeadlinePagesPerWordShift;
        _deadlinePageBits = new long[_deadlinePageWordCount];
        _deadlineMarkerCacheId = Interlocked.Increment(ref s_nextDeadlineMarkerCacheId);
        _indexMask = capacity - 1;
''',
    label="initialize compact bitmap and cache id")

old_marker = '''    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkDeadlinePage(int index)
    {
        var page = index >> DeadlinePageShift;
        var stripe = Environment.CurrentManagedThreadId & DeadlineRegistrationStripeMask;
        var stripeBase = stripe * _deadlineMarkerStripeStride;
        var encodedPage = (long)page + 1;
        if (Volatile.Read(ref _deadlinePageBits[stripeBase]) == encodedPage)
            return;

        ref var bits = ref _deadlinePageBits[
            stripeBase + 1 + (page >> DeadlinePagesPerWordShift)];
        var bit = 1L << (page & (DeadlinePagesPerWord - 1));
        var current = Volatile.Read(ref bits);
        while ((current & bit) == 0)
        {
            var updated = current | bit;
            var observed = Interlocked.CompareExchange(ref bits, updated, current);
            if (observed == current)
                break;
            current = observed;
        }

        Volatile.Write(ref _deadlinePageBits[stripeBase], encodedPage);
    }
'''
new_marker = '''    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkDeadlinePage(int index)
    {
        var page = index >> DeadlinePageShift;
        var epoch = Volatile.Read(ref _deadlineMarkerEpoch);
        if (t_deadlineMarkerCacheId == _deadlineMarkerCacheId &&
            t_deadlineMarkerCacheEpoch == epoch &&
            t_deadlineMarkerCachePage == page)
        {
            return;
        }

        ref var bits = ref _deadlinePageBits[page >> DeadlinePagesPerWordShift];
        var bit = 1L << (page & (DeadlinePagesPerWord - 1));
        var current = Volatile.Read(ref bits);
        while ((current & bit) == 0)
        {
            var updated = current | bit;
            var observed = Interlocked.CompareExchange(ref bits, updated, current);
            if (observed == current)
                break;
            current = observed;
        }

        t_deadlineMarkerCacheId = _deadlineMarkerCacheId;
        t_deadlineMarkerCacheEpoch = epoch;
        t_deadlineMarkerCachePage = page;
    }
'''
source = replace_exact(source, old_marker, new_marker, label="replace striped marker with TLS marker")

source = replace_exact(
    source,
    '''        try
        {
            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);
''',
    '''        try
        {
            // Invalidate all per-thread locality hits before any live page bit is consumed.
            Interlocked.Increment(ref _deadlineMarkerEpoch);
            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);
''',
    label="advance epoch before scan consumption")

loop_start = source.index(
    "            for (var wordIndex = 0; wordIndex < _deadlinePageWordCount; wordIndex++)\n")
loop_end = source.index(
    "            LastDeadlineScanInspectedSlots = inspectedSlots;", loop_start)
new_loop = '''            for (var wordIndex = 0; wordIndex < _deadlinePageWordCount; wordIndex++)
            {
                var pages = (ulong)Interlocked.Exchange(ref _deadlinePageBits[wordIndex], 0);
                while (pages != 0)
                {
                    var bitIndex = System.Numerics.BitOperations.TrailingZeroCount(pages);
                    pages &= pages - 1;
                    var page = (wordIndex << DeadlinePagesPerWordShift) + bitIndex;
                    var start = page << DeadlinePageShift;
                    if (start >= slots.Length)
                        continue;

                    var end = Math.Min(start + DeadlinePageSize, slots.Length);
                    var hasFutureDeadline = false;
                    for (var index = start; index < end; index++)
                    {
                        inspectedSlots++;
                        var call = Volatile.Read(ref slots[index]);
                        if (call is null || !call.Deadline.HasValue)
                            continue;
                        if (call.Deadline.Timestamp <= now)
                        {
                            TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);
                        }
                        else
                        {
                            hasFutureDeadline = true;
                            UpdateEarliestDeadline(call.Deadline.Timestamp);
                        }
                    }

                    if (hasFutureDeadline)
                    {
                        var pageBit = 1L << bitIndex;
                        Interlocked.Or(ref _deadlinePageBits[wordIndex], pageBit);
                    }
                }
            }

'''
source = source[:loop_start] + new_loop + source[loop_end:]
source_path.write_text(source, encoding="utf-8")

# Benchmark iteration reset emulates the production scanner protocol: advance epoch first, then clear bits.
# On eager dev the reflection fields do not exist, so this remains a no-op there.
for benchmark_path in benchmark_paths:
    benchmark = benchmark_path.read_text(encoding="utf-8")
    benchmark = replace_exact(
        benchmark,
        "    private FieldInfo? _deadlinePageHintField;\n",
        "    private FieldInfo? _deadlinePageHintField;\n"
        "    private FieldInfo? _deadlineMarkerEpochField;\n",
        label=f"{benchmark_path}: add epoch reflection field")
    benchmark = replace_exact(
        benchmark,
        '''        _deadlinePageHintField = typeof(PendingRequestTable)
            .GetField("_deadlinePageHint", BindingFlags.Instance | BindingFlags.NonPublic);
''',
        '''        _deadlinePageHintField = typeof(PendingRequestTable)
            .GetField("_deadlinePageHint", BindingFlags.Instance | BindingFlags.NonPublic);
        _deadlineMarkerEpochField = typeof(PendingRequestTable)
            .GetField("_deadlineMarkerEpoch", BindingFlags.Instance | BindingFlags.NonPublic);
''',
        label=f"{benchmark_path}: capture epoch reflection field")
    benchmark = replace_exact(
        benchmark,
        '''        if (_deadlinePageBits is not null)
            Array.Clear(_deadlinePageBits);
        _deadlinePageHintField?.SetValue(_pending, -1);
''',
        '''        if (_deadlineMarkerEpochField is not null)
        {
            var epoch = (long)_deadlineMarkerEpochField.GetValue(_pending)!;
            _deadlineMarkerEpochField.SetValue(_pending, epoch + 1);
        }
        if (_deadlinePageBits is not null)
            Array.Clear(_deadlinePageBits);
        _deadlinePageHintField?.SetValue(_pending, -1);
''',
        label=f"{benchmark_path}: invalidate TLS epoch before bitmap reset")
    benchmark_path.write_text(benchmark, encoding="utf-8")
PY

echo "[issue252-deadline] experimental_candidate=thread-static-cache-plus-scanner-epoch"

dotnet build "$BASE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
dotnet build "$CANDIDATE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
# Correctness before performance.
dotnet test --project "$CANDIDATE/test/SharpLink.UnitTests/SharpLink.UnitTests.csproj" -c Release

run_bench() {
  local dir="$1"
  local variant="$2"
  local round="$3"
  local artifacts="$OUT/${round}-${variant}"
  mkdir -p "$artifacts"
  echo "[issue252-deadline] round=$round variant=$variant"
  (
    cd "$dir"
    dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
      -c Release --no-build -- \
      --filter '*PendingRequestDeadlineBenchmarks*' \
      --artifacts "$artifacts"
  )
}

# Alternate ordering to limit hosted-run drift bias.
run_bench "$BASE" eager-dev 1
run_bench "$CANDIDATE" tls-epoch 1
run_bench "$CANDIDATE" tls-epoch 2
run_bench "$BASE" eager-dev 2
run_bench "$BASE" eager-dev 3
run_bench "$CANDIDATE" tls-epoch 3

python3 - "$OUT" <<'PY'
import csv
import glob
import json
import statistics
import sys
from pathlib import Path

root = Path(sys.argv[1])
SINGLE = "RegisterAndCompleteWithLongDeadline"
CONTENTION = "RegisterAndCompleteLongDeadlinesWithinOnePage"

def split_number(value):
    value = (value or "").strip()
    if value in ("", "-", "NA", "N/A"):
        return 0.0, ""
    parts = value.replace(",", "").split()
    return float(parts[0]), parts[1] if len(parts) > 1 else ""

def to_ns(value):
    number, unit = split_number(value)
    scale = {"ns":1.0,"us":1_000.0,"µs":1_000.0,"μs":1_000.0,"ms":1_000_000.0,"s":1_000_000_000.0}
    if unit not in scale:
        raise SystemExit(f"unknown time unit {unit!r} in {value!r}")
    return number * scale[unit]

def to_bytes(value):
    number, unit = split_number(value)
    scale = {"":1.0,"B":1.0,"KB":1024.0,"MB":1024.0*1024.0,"GB":1024.0*1024.0*1024.0}
    if unit not in scale:
        raise SystemExit(f"unknown allocation unit {unit!r} in {value!r}")
    return number * scale[unit]

def rows_for(round_number, variant):
    files = glob.glob(str(root / f"{round_number}-{variant}" / "results" / "*-report.csv"))
    if len(files) != 1:
        raise SystemExit(f"expected one benchmark CSV for round={round_number} variant={variant}: {files}")
    with open(files[0], newline="", encoding="utf-8-sig") as handle:
        rows = list(csv.DictReader(handle))
    by_method = {row["Method"]: row for row in rows}
    for method in (SINGLE, CONTENTION):
        if method not in by_method:
            raise SystemExit(f"missing benchmark row {method} in {files[0]}")
    return by_method

def sample(row):
    return {"ns":to_ns(row["Mean"]), "allocated_b":to_bytes(row.get("Allocated", "0"))}

rounds = []
for n in (1,2,3):
    base = rows_for(n, "eager-dev")
    cand = rows_for(n, "tls-epoch")
    bs, cs = sample(base[SINGLE]), sample(cand[SINGLE])
    bc, cc = sample(base[CONTENTION]), sample(cand[CONTENTION])
    rounds.append({
        "round":n,
        "single":{"base_ns":bs["ns"],"candidate_ns":cs["ns"],"delta_percent":(cs["ns"]/bs["ns"]-1.0)*100.0,"base_allocated_b":bs["allocated_b"],"candidate_allocated_b":cs["allocated_b"]},
        "same_page_contention":{"base_ns":bc["ns"],"candidate_ns":cc["ns"],"delta_percent":(cc["ns"]/bc["ns"]-1.0)*100.0,"base_allocated_b":bc["allocated_b"],"candidate_allocated_b":cc["allocated_b"]},
    })

single = [r["single"]["delta_percent"] for r in rounds]
contention = [r["same_page_contention"]["delta_percent"] for r in rounds]
zero_alloc = all(r[s][k] == 0.0 for r in rounds for s in ("single","same_page_contention") for k in ("base_allocated_b","candidate_allocated_b"))
result = {
    "experiment":"deadline-bearing-registration-completion-tls-epoch",
    "gate_percent":3.0,
    "rounds":rounds,
    "median_single_delta_percent":statistics.median(single),
    "single_rounds_within_gate":sum(x <= 3.0 for x in single),
    "median_same_page_contention_delta_percent":statistics.median(contention),
    "same_page_contention_rounds_within_gate":sum(x <= 3.0 for x in contention),
    "zero_allocation":zero_alloc,
}
result["passed"] = (
    result["median_single_delta_percent"] <= 3.0 and result["single_rounds_within_gate"] >= 2 and
    result["median_same_page_contention_delta_percent"] <= 3.0 and result["same_page_contention_rounds_within_gate"] >= 2 and
    zero_alloc
)
print("DEADLINE_MAINTENANCE_RESULT=" + json.dumps(result, separators=(",",":")))
if not result["passed"]:
    raise SystemExit("TLS+epoch deadline-bearing PendingRequestTable maintenance missed the predeclared gate")
PY
