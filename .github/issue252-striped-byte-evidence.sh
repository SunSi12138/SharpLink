#!/usr/bin/env bash
set -euo pipefail

ROOT="$GITHUB_WORKSPACE"
BRANCH="agent/issue-252-striped-byte-markers"
BASE="$RUNNER_TEMP/issue252-striped-byte-base"
CANDIDATE="$RUNNER_TEMP/issue252-striped-byte-candidate"
OUT="$RUNNER_TEMP/issue252-striped-byte-results"
BASELINE_SHA="9b6f627954ec5a0eaca31b4cea5accdd4a6d79c9"

rm -rf "$BASE" "$CANDIDATE" "$OUT"
mkdir -p "$OUT"

git fetch --no-tags origin dev "$BRANCH"
git worktree add --detach "$BASE" origin/dev
git worktree add --detach "$CANDIDATE" "origin/$BRANCH"
trap 'git -C "$ROOT" worktree remove --force "$BASE" >/dev/null 2>&1 || true; git -C "$ROOT" worktree remove --force "$CANDIDATE" >/dev/null 2>&1 || true' EXIT

echo "[issue252-striped-byte] base_sha=$(git -C "$BASE" rev-parse HEAD)"
echo "[issue252-striped-byte] candidate_sha=$(git -C "$CANDIDATE" rev-parse HEAD)"

if [[ "$(git -C "$BASE" rev-parse HEAD)" != "$BASELINE_SHA" ]]; then
  echo "dev advanced; refresh the baseline contract before evaluating this candidate" >&2
  exit 1
fi

# Use byte-for-byte identical benchmark harnesses in the dev control and candidate worktrees.
cp "$ROOT/test/SharpLink.Benchmarks/DeadlineWorkloadEvidenceRunner.cs" \
   "$BASE/test/SharpLink.Benchmarks/DeadlineWorkloadEvidenceRunner.cs"
cp "$ROOT/test/SharpLink.Benchmarks/Program.cs" \
   "$BASE/test/SharpLink.Benchmarks/Program.cs"
cp "$ROOT/test/SharpLink.Benchmarks/PendingRequestDeadlineBenchmarks.cs" \
   "$BASE/test/SharpLink.Benchmarks/PendingRequestDeadlineBenchmarks.cs"

# Apply the theory-selected production candidate only in the candidate worktree.
python3 "$CANDIDATE/.github/issue252-striped-byte-candidate.py"

echo "[issue252-striped-byte] experimental_candidate=thread-striped-cacheline-separated-byte-page-marks"
git -C "$CANDIDATE" diff -- src/SharpLink.Client/PendingRequestTable.cs

# Correctness precedes performance.
dotnet build "$BASE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
dotnet build "$CANDIDATE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
dotnet test --project "$CANDIDATE/test/SharpLink.UnitTests/SharpLink.UnitTests.csproj" -c Release

run_maintenance_bench() {
  local dir="$1"
  local variant="$2"
  local round="$3"
  local artifacts="$OUT/maintenance-${round}-${variant}"
  mkdir -p "$artifacts"
  echo "[issue252-striped-byte] maintenance round=$round variant=$variant"
  (
    cd "$dir"
    dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
      -c Release --no-build -- \
      --filter '*PendingRequestDeadlineBenchmarks*' \
      --artifacts "$artifacts"
  )
}

# Preserve the corrected fresh/re-armed 3-pair registration/completion guardrail.
run_maintenance_bench "$BASE" eager-dev 1
run_maintenance_bench "$CANDIDATE" striped-byte 1
run_maintenance_bench "$CANDIDATE" striped-byte 2
run_maintenance_bench "$BASE" eager-dev 2
run_maintenance_bench "$BASE" eager-dev 3
run_maintenance_bench "$CANDIDATE" striped-byte 3

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
    scale = {"": 1.0, "B": 1.0, "KB": 1024.0, "MB": 1024.0 * 1024.0, "GB": 1024.0 * 1024.0 * 1024.0}
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
    for method in (SINGLE, CONTENTION):
        if method not in by_method:
            raise SystemExit(f"missing benchmark row {method} in {files[0]}")
    return by_method


def sample(row):
    return {"ns": to_ns(row["Mean"]), "allocated_b": to_bytes(row.get("Allocated", "0"))}

rounds = []
for n in (1, 2, 3):
    base = rows_for(n, "eager-dev")
    cand = rows_for(n, "striped-byte")
    bs, cs = sample(base[SINGLE]), sample(cand[SINGLE])
    bc, cc = sample(base[CONTENTION]), sample(cand[CONTENTION])
    rounds.append({
        "round": n,
        "single": {
            "base_ns": bs["ns"], "candidate_ns": cs["ns"],
            "delta_percent": (cs["ns"] / bs["ns"] - 1.0) * 100.0,
            "base_allocated_b": bs["allocated_b"], "candidate_allocated_b": cs["allocated_b"]},
        "same_page_contention": {
            "base_ns": bc["ns"], "candidate_ns": cc["ns"],
            "delta_percent": (cc["ns"] / bc["ns"] - 1.0) * 100.0,
            "base_allocated_b": bc["allocated_b"], "candidate_allocated_b": cc["allocated_b"]},
    })

single = [r["single"]["delta_percent"] for r in rounds]
contention = [r["same_page_contention"]["delta_percent"] for r in rounds]
zero_alloc = all(
    r[scenario][key] == 0.0
    for r in rounds
    for scenario in ("single", "same_page_contention")
    for key in ("base_allocated_b", "candidate_allocated_b"))
result = {
    "experiment": "deadline-bearing-registration-completion-striped-byte",
    "gate_percent": 3.0,
    "rounds": rounds,
    "median_single_delta_percent": statistics.median(single),
    "single_rounds_within_gate": sum(x <= 3.0 for x in single),
    "median_same_page_contention_delta_percent": statistics.median(contention),
    "same_page_contention_rounds_within_gate": sum(x <= 3.0 for x in contention),
    "zero_allocation": zero_alloc,
}
result["passed"] = (
    result["median_single_delta_percent"] <= 3.0 and result["single_rounds_within_gate"] >= 2 and
    result["median_same_page_contention_delta_percent"] <= 3.0 and result["same_page_contention_rounds_within_gate"] >= 2 and
    zero_alloc)
print("DEADLINE_MAINTENANCE_RESULT=" + json.dumps(result, separators=(",", ":")))
(root / "maintenance-result.json").write_text(json.dumps(result, indent=2))
if not result["passed"]:
    raise SystemExit("striped-byte candidate missed the predeclared fresh/re-armed maintenance gate")
PY

run_workload() {
  local dir="$1"
  local variant="$2"
  local round="$3"
  local json="$OUT/workload-${round}-${variant}.json"
  echo "[issue252-striped-byte] workload round=$round variant=$variant"
  (
    cd "$dir"
    dotnet run -c Release --no-build \
      --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -- \
      --deadline-workload-evidence \
      --rounds 1 \
      --warmup-seconds 2 \
      --duration-seconds 8 \
      --json "$json" \
      --production-baseline-sha "$BASELINE_SHA"
  )
}

# Only pay for the comprehensive workload after the candidate survives the historical guardrail.
run_workload "$BASE" eager-dev 1
run_workload "$CANDIDATE" striped-byte 1
run_workload "$CANDIDATE" striped-byte 2
run_workload "$BASE" eager-dev 2
run_workload "$BASE" eager-dev 3
run_workload "$CANDIDATE" striped-byte 3

python3 - "$OUT" <<'PY'
import json
import statistics
import sys
from pathlib import Path

root = Path(sys.argv[1])
scenarios = ("single-fast", "concurrent-fast", "concurrent-normal", "deadline-heavy")
scan_dominated = {"single-fast", "concurrent-fast", "deadline-heavy"}


def load(round_number, variant):
    report = json.loads((root / f"workload-{round_number}-{variant}.json").read_text())
    return {row["Scenario"]: row for row in report["Summaries"]}

pairs = []
for n in (1, 2, 3):
    base = load(n, "eager-dev")
    cand = load(n, "striped-byte")
    pair = {"round": n, "scenarios": {}}
    for scenario in scenarios:
        b, c = base[scenario], cand[scenario]
        pair["scenarios"][scenario] = {
            "base_qps": b["MedianQps"],
            "candidate_qps": c["MedianQps"],
            "qps_delta_percent": (c["MedianQps"] / b["MedianQps"] - 1.0) * 100.0,
            "base_cpu_ns_op": b["MedianCpuNanosecondsPerOperation"],
            "candidate_cpu_ns_op": c["MedianCpuNanosecondsPerOperation"],
            "cpu_delta_percent": (c["MedianCpuNanosecondsPerOperation"] / b["MedianCpuNanosecondsPerOperation"] - 1.0) * 100.0,
            "base_b_op": b["MedianAllocatedBytesPerOperation"],
            "candidate_b_op": c["MedianAllocatedBytesPerOperation"],
            "allocated_delta_b_op": c["MedianAllocatedBytesPerOperation"] - b["MedianAllocatedBytesPerOperation"],
            "base_p99_late_ms": b["MedianP99LatenessMilliseconds"],
            "candidate_p99_late_ms": c["MedianP99LatenessMilliseconds"],
            "p99_late_delta_ms": c["MedianP99LatenessMilliseconds"] - b["MedianP99LatenessMilliseconds"],
        }
    pairs.append(pair)

summary = {}
for scenario in scenarios:
    rows = [pair["scenarios"][scenario] for pair in pairs]
    summary[scenario] = {
        "median_qps_delta_percent": statistics.median(r["qps_delta_percent"] for r in rows),
        "qps_rounds_not_worse_than_3_percent": sum(r["qps_delta_percent"] >= -3.0 for r in rows),
        "median_cpu_delta_percent": statistics.median(r["cpu_delta_percent"] for r in rows),
        "cpu_rounds_not_worse_than_3_percent": sum(r["cpu_delta_percent"] <= 3.0 for r in rows),
        "median_allocated_delta_b_op": statistics.median(r["allocated_delta_b_op"] for r in rows),
        "median_p99_late_delta_ms": statistics.median(r["p99_late_delta_ms"] for r in rows),
    }

no_cpu_regression = all(
    row["median_cpu_delta_percent"] <= 3.0 and row["cpu_rounds_not_worse_than_3_percent"] >= 2
    for row in summary.values())
no_qps_regression = all(
    row["median_qps_delta_percent"] >= -3.0 and row["qps_rounds_not_worse_than_3_percent"] >= 2
    for row in summary.values())
no_allocation_regression = all(row["median_allocated_delta_b_op"] <= 1.0 for row in summary.values())
no_lateness_regression = all(row["median_p99_late_delta_ms"] <= 0.5 for row in summary.values())
meaningful_cpu_win = any(summary[name]["median_cpu_delta_percent"] <= -5.0 for name in scan_dominated)

result = {
    "experiment": "combined-deadline-workload-striped-byte",
    "pairs": pairs,
    "summary": summary,
    "criteria": {
        "no_scenario_median_cpu_regression_over_percent": 3.0,
        "minimum_rounds_within_cpu_guardrail": 2,
        "no_scenario_median_qps_regression_below_percent": -3.0,
        "minimum_rounds_within_qps_guardrail": 2,
        "max_median_allocation_regression_b_op": 1.0,
        "max_median_p99_lateness_regression_ms": 0.5,
        "required_scan_dominated_cpu_improvement_percent": -5.0,
    },
    "no_cpu_regression": no_cpu_regression,
    "no_qps_regression": no_qps_regression,
    "no_allocation_regression": no_allocation_regression,
    "no_lateness_regression": no_lateness_regression,
    "meaningful_cpu_win": meaningful_cpu_win,
}
result["passed"] = all((
    no_cpu_regression,
    no_qps_regression,
    no_allocation_regression,
    no_lateness_regression,
    meaningful_cpu_win,
))
print("DEADLINE_WORKLOAD_RESULT=" + json.dumps(result, separators=(",", ":")))
(root / "workload-result.json").write_text(json.dumps(result, indent=2))
if not result["passed"]:
    raise SystemExit("striped-byte candidate did not demonstrate a net combined deadline-workload win")
PY
