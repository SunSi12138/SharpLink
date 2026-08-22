#!/usr/bin/env bash
set -euo pipefail

ROOT="$GITHUB_WORKSPACE"
DEV_SHA="9b6f627954ec5a0eaca31b4cea5accdd4a6d79c9"
BASE="$RUNNER_TEMP/issue252-scanner-index-base"
CANDIDATE="$RUNNER_TEMP/issue252-scanner-index-candidate"
OUT="$RUNNER_TEMP/issue252-scanner-index-results"

rm -rf "$BASE" "$CANDIDATE" "$OUT"
mkdir -p "$OUT"
git fetch --no-tags origin dev
ACTUAL_DEV_SHA="$(git rev-parse origin/dev)"
if [[ "$ACTUAL_DEV_SHA" != "$DEV_SHA" ]]; then
  echo "dev moved: expected $DEV_SHA, got $ACTUAL_DEV_SHA" >&2
  exit 1
fi

git worktree add --detach "$BASE" "$DEV_SHA"
git worktree add --detach "$CANDIDATE" "$DEV_SHA"
cleanup() {
  git -C "$ROOT" worktree remove --force "$BASE" >/dev/null 2>&1 || true
  git -C "$ROOT" worktree remove --force "$CANDIDATE" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "[issue252-index] base_sha=$(git -C "$BASE" rev-parse HEAD)"
echo "[issue252-index] candidate_source_sha=$(git -C "$CANDIDATE" rev-parse HEAD)"

# Both variants use byte-for-byte identical benchmark sources. The only runtime difference is the
# PendingRequestTable prototype applied below to the candidate worktree.
for dir in "$BASE" "$CANDIDATE"; do
  cp "$ROOT/test/SharpLink.Benchmarks/DeadlineWorkloadEvidenceRunner.cs" \
     "$dir/test/SharpLink.Benchmarks/DeadlineWorkloadEvidenceRunner.cs"
  cp "$ROOT/test/SharpLink.Benchmarks/PendingRequestDeadlineBenchmarks.cs" \
     "$dir/test/SharpLink.Benchmarks/PendingRequestDeadlineBenchmarks.cs"
  cp "$ROOT/test/SharpLink.Benchmarks/Program.cs" \
     "$dir/test/SharpLink.Benchmarks/Program.cs"
done

python3 - "$CANDIDATE/src/SharpLink.Client/PendingRequestTable.cs" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
text = path.read_text()

def one(old: str, new: str) -> None:
    global text
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"expected exactly one patch match, got {count}: {old[:120]!r}")
    text = text.replace(old, new, 1)

one(
'''internal sealed class PendingRequestTable : IDisposable
{
    private readonly int _indexMask;
''',
'''internal sealed class PendingRequestTable : IDisposable
{
    // Scanner-owned page metadata. Registrations never mark pages; they only keep using the existing
    // approximate-earliest update. A full scan indexes pages that contain future deadlines. Later
    // callbacks may scan only those pages until a post-index registration becomes due, at which point
    // its existing earliest-deadline signal forces another full rebuild.
    private const int DeadlineScanPageShift = 8;
    private const int DeadlineScanPageSize = 1 << DeadlineScanPageShift;
    private readonly int _indexMask;
''')

one(
'''    private long _nextId;
    private long _approximateEarliestDeadline = long.MaxValue;
    private int _deadlineScanRunning;
''',
'''    private long _nextId;
    // Between full scans this is the earliest deadline registered after the scanner rebuilt its page
    // index. It deliberately stays on the existing registration path so the prototype adds no second
    // per-deadline marker write/read.
    private long _approximateEarliestDeadline = long.MaxValue;
    private long _scheduledDeadline = long.MaxValue;
    private ulong[]? _deadlineScanPages;
    private int _deadlineScanRunning;
''')

old = '''    private void UpdateEarliestDeadline(long deadlineTimestamp)
    {
        while (true)
        {
            var current = Volatile.Read(ref _approximateEarliestDeadline);
            if (current <= deadlineTimestamp)
                return;
            if (Interlocked.CompareExchange(
                    ref _approximateEarliestDeadline,
                    deadlineTimestamp,
                    current) != current)
            {
                continue;
            }

            ArmDeadlineTimer(deadlineTimestamp);
            return;
        }
    }

    private void ScanExpiredDeadlines()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.CompareExchange(ref _deadlineScanRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);
            var slots = Volatile.Read(ref _slots);
            if (slots is null)
                return;

            var now = _timeProvider.GetTimestamp();
            for (var index = 0; index < slots.Length; index++)
            {
                var call = Volatile.Read(ref slots[index]);
                if (call is null || !call.Deadline.HasValue)
                    continue;
                if (call.Deadline.Timestamp <= now)
                {
                    TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);
                }
                else
                {
                    UpdateEarliestDeadline(call.Deadline.Timestamp);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _deadlineScanRunning, 0);
            var next = Volatile.Read(ref _approximateEarliestDeadline);
            if (next != long.MaxValue)
                ArmDeadlineTimer(next);
        }
    }

    private void ArmDeadlineTimer(long deadlineTimestamp)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var delay = RpcDeadline.GetRemaining(
            deadlineTimestamp,
            _timeProvider.GetTimestamp(),
            _timeProvider.TimestampFrequency);
        if (delay > SharpLinkTimer.MaximumDelay)
            delay = SharpLinkTimer.MaximumDelay;
        try
        {
            _deadlineTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }
'''

new = '''    private void UpdateEarliestDeadline(long deadlineTimestamp)
    {
        while (true)
        {
            var current = Volatile.Read(ref _approximateEarliestDeadline);
            if (current <= deadlineTimestamp)
                return;
            if (Interlocked.CompareExchange(
                    ref _approximateEarliestDeadline,
                    deadlineTimestamp,
                    current) != current)
            {
                continue;
            }

            ArmDeadlineTimerIfEarlier(deadlineTimestamp);
            return;
        }
    }

    private void ScanExpiredDeadlines()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (Interlocked.CompareExchange(ref _deadlineScanRunning, 1, 0) != 0)
        {
            // A one-shot callback can race a scan that is already running. Invalidate the cached arm
            // target so that the running scanner deterministically re-arms from its tracked pages plus
            // the registration-owned earliest value instead of assuming this consumed callback remains.
            Interlocked.Exchange(ref _scheduledDeadline, long.MaxValue);
            return;
        }

        var trackedNext = long.MaxValue;
        try
        {
            Interlocked.Exchange(ref _scheduledDeadline, long.MaxValue);
            var slots = Volatile.Read(ref _slots);
            if (slots is null)
                return;

            var now = _timeProvider.GetTimestamp();
            var pages = _deadlineScanPages;
            var unindexedEarliest = Volatile.Read(ref _approximateEarliestDeadline);
            if (pages is null || unindexedEarliest <= now)
            {
                // Reset before the full walk. A registration published before this exchange is visible
                // to the walk; one published after it updates _approximateEarliestDeadline after slot
                // publication, so neither side of the race can disappear from future scheduling.
                Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);
                pages = GetOrCreateDeadlineScanPages();
                trackedNext = ScanAllDeadlineSlots(slots, pages, now);
            }
            else
            {
                trackedNext = ScanIndexedDeadlinePages(slots, pages, now);
            }
        }
        finally
        {
            Volatile.Write(ref _deadlineScanRunning, 0);

            // Re-read after publishing scan completion. A registration that raced the tail of the scan
            // either armed itself or is represented by this value; taking the minimum cannot delay it.
            var unindexedNext = Volatile.Read(ref _approximateEarliestDeadline);
            var next = Math.Min(trackedNext, unindexedNext);
            if (next != long.MaxValue)
                ArmDeadlineTimerIfEarlier(next);
        }
    }

    private long ScanAllDeadlineSlots(PendingCall?[] slots, ulong[] pages, long now)
    {
        Array.Clear(pages);
        var next = long.MaxValue;
        for (var index = 0; index < slots.Length; index++)
        {
            var call = Volatile.Read(ref slots[index]);
            if (call is null || !call.Deadline.HasValue)
                continue;

            var deadlineTimestamp = call.Deadline.Timestamp;
            if (deadlineTimestamp <= now)
            {
                TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);
                continue;
            }

            MarkDeadlineScanPage(pages, index);
            if (deadlineTimestamp < next)
                next = deadlineTimestamp;
        }
        return next;
    }

    private long ScanIndexedDeadlinePages(PendingCall?[] slots, ulong[] pages, long now)
    {
        var next = long.MaxValue;
        for (var wordIndex = 0; wordIndex < pages.Length; wordIndex++)
        {
            var word = pages[wordIndex];
            pages[wordIndex] = 0;
            while (word != 0)
            {
                var bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                word &= word - 1;
                var pageIndex = (wordIndex << 6) + bit;
                var pageStart = pageIndex << DeadlineScanPageShift;
                if (pageStart >= slots.Length)
                    continue;
                var pageEnd = Math.Min(pageStart + DeadlineScanPageSize, slots.Length);
                var pageNext = ScanDeadlinePage(slots, pages, pageStart, pageEnd, now);
                if (pageNext < next)
                    next = pageNext;
            }
        }
        return next;
    }

    private long ScanDeadlinePage(
        PendingCall?[] slots,
        ulong[] pages,
        int start,
        int end,
        long now)
    {
        var next = long.MaxValue;
        for (var index = start; index < end; index++)
        {
            var call = Volatile.Read(ref slots[index]);
            if (call is null || !call.Deadline.HasValue)
                continue;

            var deadlineTimestamp = call.Deadline.Timestamp;
            if (deadlineTimestamp <= now)
            {
                TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);
                continue;
            }

            MarkDeadlineScanPage(pages, index);
            if (deadlineTimestamp < next)
                next = deadlineTimestamp;
        }
        return next;
    }

    private ulong[] GetOrCreateDeadlineScanPages()
    {
        var pages = _deadlineScanPages;
        if (pages is not null)
            return pages;

        var pageCount = (_capacity + DeadlineScanPageSize - 1) >> DeadlineScanPageShift;
        pages = new ulong[(pageCount + 63) >> 6];
        _deadlineScanPages = pages;
        return pages;
    }

    private static void MarkDeadlineScanPage(ulong[] pages, int slotIndex)
    {
        var pageIndex = slotIndex >> DeadlineScanPageShift;
        pages[pageIndex >> 6] |= 1UL << (pageIndex & 63);
    }

    private void ArmDeadlineTimerIfEarlier(long deadlineTimestamp)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        while (true)
        {
            var current = Volatile.Read(ref _scheduledDeadline);
            if (current <= deadlineTimestamp)
                return;
            if (Interlocked.CompareExchange(ref _scheduledDeadline, deadlineTimestamp, current) != current)
                continue;

            // Timer.Change is serialized only when the target actually moves earlier. Registrations
            // whose deadline is not the epoch minimum still take the same read/return path as dev.
            lock (_slotsInitializationGate)
            {
                var target = Volatile.Read(ref _scheduledDeadline);
                if (target != long.MaxValue && Volatile.Read(ref _disposed) == 0)
                    ArmDeadlineTimerCore(target);
            }
            return;
        }
    }

    private void ArmDeadlineTimerCore(long deadlineTimestamp)
    {
        var delay = RpcDeadline.GetRemaining(
            deadlineTimestamp,
            _timeProvider.GetTimestamp(),
            _timeProvider.TimestampFrequency);
        if (delay > SharpLinkTimer.MaximumDelay)
            delay = SharpLinkTimer.MaximumDelay;
        try
        {
            _deadlineTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }
'''

one(old, new)
path.write_text(text)
PY

# Build both variants from the same dev production tree plus identical evidence sources.
for dir in "$BASE" "$CANDIDATE"; do
  dotnet restore "$dir/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
  dotnet build "$dir/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" \
    -c Release --no-restore -v minimal
done

run_workload() {
  local dir="$1"
  local variant="$2"
  local round="$3"
  local json="$OUT/workload-${round}-${variant}.json"
  echo "[issue252-index] workload round=$round variant=$variant"
  (
    cd "$dir"
    dotnet run -c Release --no-build \
      --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -- \
      --deadline-workload-evidence \
      --rounds 1 \
      --warmup-seconds 2 \
      --duration-seconds 8 \
      --json "$json" \
      --production-baseline-sha "$DEV_SHA"
  )
}

# Alternate order on the same hosted runner. Each process performs the identical four-scenario
# workload; the candidate pays its first scanner-index allocation in every measured round.
run_workload "$BASE" dev 1
run_workload "$CANDIDATE" scanner-index 1
run_workload "$CANDIDATE" scanner-index 2
run_workload "$BASE" dev 2
run_workload "$BASE" dev 3
run_workload "$CANDIDATE" scanner-index 3

python3 - "$OUT" <<'PY'
import json
import math
import statistics
import sys
from pathlib import Path

root = Path(sys.argv[1])
scenarios = ["single-fast", "concurrent-fast", "concurrent-normal", "deadline-heavy"]

def report(round_number, variant):
    return json.loads((root / f"workload-{round_number}-{variant}.json").read_text())

def one_result(data, scenario):
    rows = [row for row in data["Results"] if row["Scenario"] == scenario]
    if len(rows) != 1:
        raise SystemExit(f"expected one result for {scenario}, got {len(rows)}")
    return rows[0]

paired = {scenario: [] for scenario in scenarios}
for round_number in (1, 2, 3):
    base = report(round_number, "dev")
    candidate = report(round_number, "scanner-index")
    for scenario in scenarios:
        b = one_result(base, scenario)
        c = one_result(candidate, scenario)
        paired[scenario].append({
            "round": round_number,
            "base_qps": b["Qps"],
            "candidate_qps": c["Qps"],
            "qps_delta_percent": (c["Qps"] / b["Qps"] - 1.0) * 100.0,
            "base_cpu_ns_op": b["CpuNanosecondsPerOperation"],
            "candidate_cpu_ns_op": c["CpuNanosecondsPerOperation"],
            "cpu_delta_percent": (c["CpuNanosecondsPerOperation"] / b["CpuNanosecondsPerOperation"] - 1.0) * 100.0,
            "base_b_op": b["AllocatedBytesPerOperation"],
            "candidate_b_op": c["AllocatedBytesPerOperation"],
            "allocation_delta_b_op": c["AllocatedBytesPerOperation"] - b["AllocatedBytesPerOperation"],
            "base_p95_ms": b["P95LatenessMilliseconds"],
            "candidate_p95_ms": c["P95LatenessMilliseconds"],
            "base_p99_ms": b["P99LatenessMilliseconds"],
            "candidate_p99_ms": c["P99LatenessMilliseconds"],
            "base_timer_callbacks_per_second": b["TimerCallbacksPerSecond"],
            "candidate_timer_callbacks_per_second": c["TimerCallbacksPerSecond"],
        })

summaries = {}
for scenario, rows in paired.items():
    summaries[scenario] = {
        "median_cpu_delta_percent": statistics.median(row["cpu_delta_percent"] for row in rows),
        "median_qps_delta_percent": statistics.median(row["qps_delta_percent"] for row in rows),
        "median_allocation_delta_b_op": statistics.median(row["allocation_delta_b_op"] for row in rows),
        "median_base_p95_ms": statistics.median(row["base_p95_ms"] for row in rows),
        "median_candidate_p95_ms": statistics.median(row["candidate_p95_ms"] for row in rows),
        "median_base_p99_ms": statistics.median(row["base_p99_ms"] for row in rows),
        "median_candidate_p99_ms": statistics.median(row["candidate_p99_ms"] for row in rows),
        "median_base_timer_callbacks_per_second": statistics.median(row["base_timer_callbacks_per_second"] for row in rows),
        "median_candidate_timer_callbacks_per_second": statistics.median(row["candidate_timer_callbacks_per_second"] for row in rows),
    }

scenario_cpu = [summaries[name]["median_cpu_delta_percent"] for name in scenarios]
overall_cpu_median = statistics.median(scenario_cpu)

def lateness_ok(summary, percentile):
    base = summary[f"median_base_{percentile}_ms"]
    candidate = summary[f"median_candidate_{percentile}_ms"]
    limit = base + max(0.05, base * 0.25)
    return candidate <= limit

per_scenario_ok = all(
    row["median_cpu_delta_percent"] <= 3.0
    and row["median_qps_delta_percent"] >= -3.0
    and row["median_allocation_delta_b_op"] <= 1.0
    and lateness_ok(row, "p95")
    and lateness_ok(row, "p99")
    for row in summaries.values()
)
# CPU noise in the established dev baseline is roughly 1-2%; require a result clearly outside that
# band before paying production complexity. QPS is deadline-paced, so it is a non-regression metric.
passed = overall_cpu_median <= -5.0 and per_scenario_ok
result = {
    "experiment": "scanner-owned-deadline-page-index-combined-workload",
    "dev_sha": "9b6f627954ec5a0eaca31b4cea5accdd4a6d79c9",
    "predeclared_gate": {
        "overall_median_cpu_delta_percent_max": -5.0,
        "per_scenario_cpu_regression_percent_max": 3.0,
        "per_scenario_qps_regression_percent_max": 3.0,
        "allocation_delta_b_op_max": 1.0,
        "lateness_limit": "candidate <= base + max(0.05 ms, 25% of base)",
    },
    "rounds": paired,
    "summaries": summaries,
    "overall_median_cpu_delta_percent": overall_cpu_median,
    "passed": passed,
}
(root / "combined-result.json").write_text(json.dumps(result, indent=2))
print("COMBINED_WORKLOAD_RESULT=" + json.dumps(result, separators=(",", ":")))
if not passed:
    raise SystemExit("scanner-owned page index did not produce a material net combined-workload win")
PY

run_maintenance_bench() {
  local dir="$1"
  local variant="$2"
  local round="$3"
  local artifacts="$OUT/maintenance-${round}-${variant}"
  mkdir -p "$artifacts"
  echo "[issue252-index] maintenance round=$round variant=$variant"
  (
    cd "$dir"
    dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
      -c Release --no-build -- \
      --filter '*PendingRequestDeadlineBenchmarks*' \
      --artifacts "$artifacts"
  )
}

# Only pay for the long BenchmarkDotNet guardrail after the combined-workload design gate passes.
run_maintenance_bench "$BASE" dev 1
run_maintenance_bench "$CANDIDATE" scanner-index 1
run_maintenance_bench "$CANDIDATE" scanner-index 2
run_maintenance_bench "$BASE" dev 2
run_maintenance_bench "$BASE" dev 3
run_maintenance_bench "$CANDIDATE" scanner-index 3

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
    scale = {"ns": 1.0, "us": 1_000.0, "µs": 1_000.0, "μs": 1_000.0,
             "ms": 1_000_000.0, "s": 1_000_000_000.0}
    if unit not in scale:
        raise SystemExit(f"unknown time unit {unit!r} in {value!r}")
    return number * scale[unit]

def to_bytes(value):
    number, unit = split_number(value)
    scale = {"": 1.0, "B": 1.0, "KB": 1024.0, "MB": 1024.0**2, "GB": 1024.0**3}
    if unit not in scale:
        raise SystemExit(f"unknown allocation unit {unit!r} in {value!r}")
    return number * scale[unit]

def rows_for(round_number, variant):
    files = glob.glob(str(root / f"maintenance-{round_number}-{variant}" / "results" / "*-report.csv"))
    if len(files) != 1:
        raise SystemExit(f"expected one benchmark CSV for round={round_number} variant={variant}: {files}")
    with open(files[0], newline="", encoding="utf-8-sig") as handle:
        rows = list(csv.DictReader(handle))
    by_method = {row["Method"]: row for row in rows}
    return by_method

def sample(row):
    return {"ns": to_ns(row["Mean"]), "allocated_b": to_bytes(row.get("Allocated", "0"))}

rounds = []
for round_number in (1, 2, 3):
    base = rows_for(round_number, "dev")
    candidate = rows_for(round_number, "scanner-index")
    for method in (SINGLE, CONTENTION):
        if method not in base or method not in candidate:
            raise SystemExit(f"missing {method} in maintenance benchmark output")
    bs = sample(base[SINGLE]); cs = sample(candidate[SINGLE])
    bc = sample(base[CONTENTION]); cc = sample(candidate[CONTENTION])
    rounds.append({
        "round": round_number,
        "single": {
            "base_ns": bs["ns"], "candidate_ns": cs["ns"],
            "delta_percent": (cs["ns"] / bs["ns"] - 1.0) * 100.0,
            "base_allocated_b": bs["allocated_b"], "candidate_allocated_b": cs["allocated_b"],
        },
        "same_page_contention": {
            "base_ns": bc["ns"], "candidate_ns": cc["ns"],
            "delta_percent": (cc["ns"] / bc["ns"] - 1.0) * 100.0,
            "base_allocated_b": bc["allocated_b"], "candidate_allocated_b": cc["allocated_b"],
        },
    })

single = [row["single"]["delta_percent"] for row in rounds]
contention = [row["same_page_contention"]["delta_percent"] for row in rounds]
zero_allocation = all(
    row[scope][key] == 0.0
    for row in rounds
    for scope in ("single", "same_page_contention")
    for key in ("base_allocated_b", "candidate_allocated_b")
)
result = {
    "experiment": "fresh-scheduler-epoch-deadline-registration-completion",
    "gate_percent": 3.0,
    "rounds": rounds,
    "median_single_delta_percent": statistics.median(single),
    "single_rounds_within_gate": sum(delta <= 3.0 for delta in single),
    "median_same_page_contention_delta_percent": statistics.median(contention),
    "same_page_contention_rounds_within_gate": sum(delta <= 3.0 for delta in contention),
    "zero_allocation": zero_allocation,
}
result["passed"] = (
    result["median_single_delta_percent"] <= 3.0
    and result["single_rounds_within_gate"] >= 2
    and result["median_same_page_contention_delta_percent"] <= 3.0
    and result["same_page_contention_rounds_within_gate"] >= 2
    and zero_allocation
)
(root / "maintenance-result.json").write_text(json.dumps(result, indent=2))
print("DEADLINE_MAINTENANCE_RESULT=" + json.dumps(result, separators=(",", ":")))
if not result["passed"]:
    raise SystemExit("fresh scheduler-epoch deadline maintenance missed the predeclared 3% / zero-allocation gate")
PY

mkdir -p "$ROOT/artifacts/issue-252-scanner-index"
cp "$OUT/combined-result.json" "$ROOT/artifacts/issue-252-scanner-index/combined-result.json"
cp "$OUT/maintenance-result.json" "$ROOT/artifacts/issue-252-scanner-index/maintenance-result.json"
