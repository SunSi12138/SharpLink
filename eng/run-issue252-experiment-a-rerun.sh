#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEMP_ROOT="${RUNNER_TEMP:-/tmp}"
EVIDENCE_ROOT="$TEMP_ROOT/issue252-experiment-a-rerun"
BASE_ROOT="$TEMP_ROOT/issue252-a-base"
CAND_ROOT="$TEMP_ROOT/issue252-a-candidate"

rm -rf "$EVIDENCE_ROOT"
mkdir -p "$EVIDENCE_ROOT"

echo "[issue252-A] preparing paired worktrees"
git -C "$ROOT" fetch --no-tags origin dev
git -C "$ROOT" worktree add --detach "$BASE_ROOT" origin/dev
git -C "$ROOT" worktree add --detach "$CAND_ROOT" HEAD
trap 'git -C "$ROOT" worktree remove --force "$BASE_ROOT" >/dev/null 2>&1 || true; git -C "$ROOT" worktree remove --force "$CAND_ROOT" >/dev/null 2>&1 || true' EXIT

cp "$ROOT/test/SharpLink.Benchmarks/PendingRequestCrossSegmentConcurrencyBenchmarks.cs" \
   "$BASE_ROOT/test/SharpLink.Benchmarks/PendingRequestCrossSegmentConcurrencyBenchmarks.cs"

BASE_SHA="$(git -C "$BASE_ROOT" rev-parse HEAD)"
CAND_SOURCE_SHA="$(git -C "$CAND_ROOT" rev-parse HEAD)"
echo "[issue252-A] base_sha=$BASE_SHA"
echo "[issue252-A] candidate_source_sha=$CAND_SOURCE_SHA"

(
  cd "$CAND_ROOT"
  python3 eng/issue252-experiment-a.py
  git diff --check
  git diff --stat
)

echo "[issue252-A] validating candidate unit tests"
if ! dotnet test --project "$CAND_ROOT/test/SharpLink.UnitTests/SharpLink.UnitTests.csproj" -c Release --nologo; then
  echo "[issue252-A] first unit-test pass failed; retrying once to separate known unrelated CI flakes"
  dotnet test --project "$CAND_ROOT/test/SharpLink.UnitTests/SharpLink.UnitTests.csproj" -c Release --nologo
fi

echo "[issue252-A] building benchmark variants"
dotnet build "$BASE_ROOT/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
dotnet build "$CAND_ROOT/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal

run_variant() {
  local round="$1"
  local name="$2"
  local root="$3"
  local out="$EVIDENCE_ROOT/round${round}-${name}"
  mkdir -p "$out"

  echo "[issue252-A] round=$round variant=$name benchmark=single"
  (
    cd "$root"
    dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release --no-build -- \
      --filter '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' \
      --artifacts "$out/single" --exporters json
  ) | tee "$out/single.log"

  echo "[issue252-A] round=$round variant=$name benchmark=cross"
  (
    cd "$root"
    dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release --no-build -- \
      --filter '*PendingRequestCrossSegmentConcurrencyBenchmarks.RegisterAndCompleteAcrossFourSegments*' \
      --artifacts "$out/cross" --exporters json
  ) | tee "$out/cross.log"
}

# Alternate order to reduce thermal / ordering bias.
run_variant 1 eager-dev "$BASE_ROOT"
run_variant 1 experiment-a "$CAND_ROOT"
run_variant 2 experiment-a "$CAND_ROOT"
run_variant 2 eager-dev "$BASE_ROOT"
run_variant 3 eager-dev "$BASE_ROOT"
run_variant 3 experiment-a "$CAND_ROOT"

python3 - "$EVIDENCE_ROOT" <<'PY'
from pathlib import Path
import json
import re
import statistics
import sys

root = Path(sys.argv[1])

def mean_ns(path: Path) -> float:
    text = path.read_text()
    matches = re.findall(r'^Mean = ([0-9.]+) (ns|us|ms)', text, re.M)
    if not matches:
        raise SystemExit(f'no BDN mean in {path}')
    value, unit = matches[-1]
    return float(value) * {'ns': 1.0, 'us': 1000.0, 'ms': 1_000_000.0}[unit]

rounds = []
for number in (1, 2, 3):
    base_single = mean_ns(root / f'round{number}-eager-dev' / 'single.log')
    cand_single = mean_ns(root / f'round{number}-experiment-a' / 'single.log')
    base_cross = mean_ns(root / f'round{number}-eager-dev' / 'cross.log')
    cand_cross = mean_ns(root / f'round{number}-experiment-a' / 'cross.log')
    rounds.append({
        'round': number,
        'base_single_ns': base_single,
        'candidate_single_ns': cand_single,
        'single_delta_percent': (cand_single / base_single - 1.0) * 100.0,
        'base_cross_ns': base_cross,
        'candidate_cross_ns': cand_cross,
        'cross_delta_percent': (cand_cross / base_cross - 1.0) * 100.0,
    })

single_deltas = [r['single_delta_percent'] for r in rounds]
cross_deltas = [r['cross_delta_percent'] for r in rounds]
median_single = statistics.median(single_deltas)
median_cross = statistics.median(cross_deltas)
single_within = sum(delta <= 3.0 for delta in single_deltas)
cross_within = sum(delta <= 3.0 for delta in cross_deltas)
passed = (
    median_single <= 3.0 and single_within >= 2 and
    median_cross <= 3.0 and cross_within >= 2
)
result = {
    'experiment': 'A-operation-local-slot-hoisting',
    'gate_percent': 3.0,
    'rounds': rounds,
    'median_single_delta_percent': median_single,
    'single_rounds_within_gate': single_within,
    'median_cross_delta_percent': median_cross,
    'cross_rounds_within_gate': cross_within,
    'passed': passed,
}
(root / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
print('EXPERIMENT_A_RESULT=' + json.dumps(result, separators=(',', ':')))
if not passed:
    raise SystemExit('Experiment A missed the stable <=3% gate')
PY
