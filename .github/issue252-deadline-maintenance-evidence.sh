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

# The candidate must be the production TLS+epoch implementation. This evidence script no longer
# rewrites production source: benchmark numbers below are for the exact branch code under review.
grep -q 't_deadlineMarkerCacheId' "$CANDIDATE/src/SharpLink.Client/PendingRequestTable.cs"
grep -q 'Interlocked.Increment(ref _deadlineMarkerEpoch)' "$CANDIDATE/src/SharpLink.Client/PendingRequestTable.cs"
echo "[issue252-deadline] candidate=production-thread-static-cache-plus-scanner-epoch"

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
