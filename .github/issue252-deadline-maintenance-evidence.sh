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

# Use the exact same benchmark source on both implementations so only PendingRequestTable differs.
cp "$ROOT/test/SharpLink.Benchmarks/PendingRequestDeadlineBenchmarks.cs" \
   "$BASE/test/SharpLink.Benchmarks/PendingRequestDeadlineBenchmarks.cs"

# Evaluate the page-local approximate-earliest design without committing it to production first.
# The candidate worktree is rewritten only for this evidence run. Registration performs an atomic min
# on one long per 256-slot page; completion does not touch page metadata. Scans read page minima and
# inspect slot ranges only when the page minimum is due. Stale minima are conservative: they can cause
# one later page scan, but cannot delay a newly registered earlier deadline because OnRegistered still
# updates the table-wide earliest timer after publishing the page minimum.
python3 - "$CANDIDATE/src/SharpLink.Client/PendingRequestTable.cs" \
          "$BASE/test/SharpLink.Benchmarks/PendingRequestDeadlineBenchmarks.cs" \
          "$CANDIDATE/test/SharpLink.Benchmarks/PendingRequestDeadlineBenchmarks.cs" \
          "$CANDIDATE/test/SharpLink.UnitTests/Runtime/PendingRequestTableStorageTests.cs" <<'PY'
from pathlib import Path
import sys

source_path = Path(sys.argv[1])
benchmark_paths = [Path(sys.argv[2]), Path(sys.argv[3])]
storage_test_path = Path(sys.argv[4])


def replace_exact(text, old, new, expected=1, label="replacement"):
    count = text.count(old)
    if count != expected:
        raise SystemExit(f"{label}: expected {expected} matches, found {count}")
    return text.replace(old, new)

source = source_path.read_text(encoding="utf-8")
source = replace_exact(
    source,
    "    private readonly long[] _deadlinePageBits;\n",
    "    private readonly long[] _deadlinePageBits;\n"
    "    private readonly long[] _deadlinePageEarliest;\n",
    label="add page-earliest field")
source = replace_exact(
    source,
    "        _deadlinePageBits = new long[_deadlineMarkerStripeStride * DeadlineMarkerStripeCount];\n",
    "        _deadlinePageBits = new long[_deadlineMarkerStripeStride * DeadlineMarkerStripeCount];\n"
    "        _deadlinePageEarliest = new long[deadlinePageCount];\n"
    "        Array.Fill(_deadlinePageEarliest, long.MaxValue);\n",
    label="initialize page-earliest field")
source = replace_exact(
    source,
    "                            MarkDeadlinePage(index);\n",
    "                            MarkDeadlinePage(index, deadline.Timestamp);\n",
    expected=2,
    label="pass deadline timestamp to page marker")

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
    private void MarkDeadlinePage(int index, long deadlineTimestamp)
    {
        ref var earliest = ref _deadlinePageEarliest[index >> DeadlinePageShift];
        var current = Volatile.Read(ref earliest);
        while (current > deadlineTimestamp)
        {
            var observed = Interlocked.CompareExchange(ref earliest, deadlineTimestamp, current);
            if (observed == current)
                return;
            current = observed;
        }
    }
'''
source = replace_exact(source, old_marker, new_marker, label="replace deadline page marker")

loop_start = source.index(
    "            for (var wordIndex = 0; wordIndex < _deadlinePageWordCount; wordIndex++)\n")
loop_end = source.index(
    "            LastDeadlineScanInspectedSlots = inspectedSlots;", loop_start)
new_loop = '''            for (var page = 0; page < _deadlinePageEarliest.Length; page++)
            {
                var pageDeadline = Volatile.Read(ref _deadlinePageEarliest[page]);
                if (pageDeadline == long.MaxValue)
                    continue;

                if (pageDeadline > now)
                {
                    UpdateEarliestDeadline(pageDeadline);
                    continue;
                }

                Interlocked.Exchange(ref _deadlinePageEarliest[page], long.MaxValue);
                var start = page << DeadlinePageShift;
                if (start >= slots.Length)
                    continue;

                var end = Math.Min(start + DeadlinePageSize, slots.Length);
                var nextPageDeadline = long.MaxValue;
                for (var index = start; index < end; index++)
                {
                    inspectedSlots++;
                    var call = Volatile.Read(ref slots[index]);
                    if (call is null || !call.Deadline.HasValue)
                        continue;

                    var deadlineTimestamp = call.Deadline.Timestamp;
                    if (deadlineTimestamp <= now)
                    {
                        TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);
                    }
                    else
                    {
                        if (deadlineTimestamp < nextPageDeadline)
                            nextPageDeadline = deadlineTimestamp;
                        UpdateEarliestDeadline(deadlineTimestamp);
                    }
                }

                if (nextPageDeadline != long.MaxValue)
                    MarkDeadlinePage(start, nextPageDeadline);
            }

'''
source = source[:loop_start] + new_loop + source[loop_end:]
source_path.write_text(source, encoding="utf-8")

for benchmark_path in benchmark_paths:
    benchmark = benchmark_path.read_text(encoding="utf-8")
    benchmark = replace_exact(
        benchmark,
        "    private long[]? _deadlinePageBits;\n",
        "    private long[]? _deadlinePageBits;\n"
        "    private long[]? _deadlinePageEarliest;\n",
        label=f"{benchmark_path}: add page-earliest reflection field")
    benchmark = replace_exact(
        benchmark,
        '''        _deadlinePageBits = (long[]?)typeof(PendingRequestTable)
            .GetField("_deadlinePageBits", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(_pending);
''',
        '''        _deadlinePageBits = (long[]?)typeof(PendingRequestTable)
            .GetField("_deadlinePageBits", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(_pending);
        _deadlinePageEarliest = (long[]?)typeof(PendingRequestTable)
            .GetField("_deadlinePageEarliest", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(_pending);
''',
        label=f"{benchmark_path}: capture page-earliest field")
    benchmark = replace_exact(
        benchmark,
        '''        if (_deadlinePageBits is not null)
            Array.Clear(_deadlinePageBits);
        _deadlinePageHintField?.SetValue(_pending, -1);
''',
        '''        if (_deadlinePageBits is not null)
            Array.Clear(_deadlinePageBits);
        if (_deadlinePageEarliest is not null)
            Array.Fill(_deadlinePageEarliest, long.MaxValue);
        _deadlinePageHintField?.SetValue(_pending, -1);
''',
        label=f"{benchmark_path}: reset page-earliest field")
    benchmark_path.write_text(benchmark, encoding="utf-8")

# The page-minimum design intentionally skips a retired page whose stale minimum is still in the future;
# the old bitmap design consumed that retired mark immediately and therefore inspected 512 slots once.
storage_test = storage_test_path.read_text(encoding="utf-8")
storage_test = replace_exact(
    storage_test,
    '''        Ensure(table.LastDeadlineScanInspectedSlots == 512,
            "the next scan should consume the retired page mark once while inspecting the active page");
''',
    '''        Ensure(table.LastDeadlineScanInspectedSlots == 256,
            "a retired page with a future stale minimum should not widen an earlier active-page scan");
''',
    label="adjust page-minimum sparse-scan expectation")
storage_test_path.write_text(storage_test, encoding="utf-8")
PY

echo "[issue252-deadline] experimental_candidate=page-local-approximate-earliest"

dotnet build "$BASE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
dotnet build "$CANDIDATE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
# Run the full unit suite against the experimental source before accepting any performance result.
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

# Alternate ordering to reduce hosted-run drift bias.
run_bench "$BASE" eager-dev 1
run_bench "$CANDIDATE" page-earliest 1
run_bench "$CANDIDATE" page-earliest 2
run_bench "$BASE" eager-dev 2
run_bench "$BASE" eager-dev 3
run_bench "$CANDIDATE" page-earliest 3

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
    scale = {
        "ns": 1.0,
        "us": 1_000.0,
        "µs": 1_000.0,
        "μs": 1_000.0,
        "ms": 1_000_000.0,
        "s": 1_000_000_000.0,
    }
    if unit not in scale:
        raise SystemExit(f"unknown time unit {unit!r} in {value!r}")
    return number * scale[unit]

def to_bytes(value):
    number, unit = split_number(value)
    scale = {
        "": 1.0,
        "B": 1.0,
        "KB": 1024.0,
        "MB": 1024.0 * 1024.0,
        "GB": 1024.0 * 1024.0 * 1024.0,
    }
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
    missing = [method for method in (SINGLE, CONTENTION) if method not in by_method]
    if missing:
        raise SystemExit(f"missing benchmark rows {missing} in {files[0]}: {list(by_method)}")
    return by_method

def sample(row):
    return {
        "ns": to_ns(row["Mean"]),
        "allocated_b": to_bytes(row.get("Allocated", "0")),
    }

rounds = []
for round_number in (1, 2, 3):
    base = rows_for(round_number, "eager-dev")
    candidate = rows_for(round_number, "page-earliest")
    base_single = sample(base[SINGLE])
    candidate_single = sample(candidate[SINGLE])
    base_contention = sample(base[CONTENTION])
    candidate_contention = sample(candidate[CONTENTION])
    rounds.append({
        "round": round_number,
        "single": {
            "base_ns": base_single["ns"],
            "candidate_ns": candidate_single["ns"],
            "delta_percent": (candidate_single["ns"] / base_single["ns"] - 1.0) * 100.0,
            "base_allocated_b": base_single["allocated_b"],
            "candidate_allocated_b": candidate_single["allocated_b"],
        },
        "same_page_contention": {
            "base_ns": base_contention["ns"],
            "candidate_ns": candidate_contention["ns"],
            "delta_percent": (candidate_contention["ns"] / base_contention["ns"] - 1.0) * 100.0,
            "base_allocated_b": base_contention["allocated_b"],
            "candidate_allocated_b": candidate_contention["allocated_b"],
        },
    })

single_deltas = [row["single"]["delta_percent"] for row in rounds]
contention_deltas = [row["same_page_contention"]["delta_percent"] for row in rounds]
single_median = statistics.median(single_deltas)
contention_median = statistics.median(contention_deltas)
single_within = sum(delta <= 3.0 for delta in single_deltas)
contention_within = sum(delta <= 3.0 for delta in contention_deltas)
zero_allocation = all(
    row[scope][key] == 0.0
    for row in rounds
    for scope in ("single", "same_page_contention")
    for key in ("base_allocated_b", "candidate_allocated_b")
)
passed = (
    single_median <= 3.0
    and single_within >= 2
    and contention_median <= 3.0
    and contention_within >= 2
    and zero_allocation
)

result = {
    "experiment": "deadline-bearing-registration-completion-page-earliest",
    "gate_percent": 3.0,
    "rounds": rounds,
    "median_single_delta_percent": single_median,
    "single_rounds_within_gate": single_within,
    "median_same_page_contention_delta_percent": contention_median,
    "same_page_contention_rounds_within_gate": contention_within,
    "zero_allocation": zero_allocation,
    "passed": passed,
}
print("DEADLINE_MAINTENANCE_RESULT=" + json.dumps(result, separators=(",", ":")))
if not passed:
    raise SystemExit("page-earliest deadline-bearing PendingRequestTable maintenance missed the predeclared gate")
PY
