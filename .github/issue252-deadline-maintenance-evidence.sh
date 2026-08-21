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

dotnet build "$BASE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
dotnet build "$CANDIDATE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal

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
run_bench "$CANDIDATE" lazy-flat-pages 1
run_bench "$CANDIDATE" lazy-flat-pages 2
run_bench "$BASE" eager-dev 2
run_bench "$BASE" eager-dev 3
run_bench "$CANDIDATE" lazy-flat-pages 3

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
    candidate = rows_for(round_number, "lazy-flat-pages")
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
    "experiment": "deadline-bearing-registration-completion",
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
    raise SystemExit("deadline-bearing PendingRequestTable maintenance missed the predeclared gate")
PY
