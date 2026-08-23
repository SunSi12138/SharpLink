#!/usr/bin/env bash
set -euo pipefail

ROOT="$(pwd)"
BASE_SHA="c89a79bf6a3acdc24dd0f3289dbbcbe84b1ab186"
CANDIDATE_SHA="c9142d35eb5e6326bf7681b64bfb55e3d9cfec88"
BASE_DIR="${RUNNER_TEMP:-/tmp}/issue245-baseline"
CANDIDATE_DIR="${RUNNER_TEMP:-/tmp}/issue245-candidate"
EVIDENCE_DIR="$ROOT/artifacts/issue245-review-evidence"
RUNNER_SOURCE="$ROOT/test/SharpLink.Benchmarks/AdmissionPartitionReviewEvidence.cs"

cleanup() {
  git -C "$ROOT" worktree remove --force "$BASE_DIR" >/dev/null 2>&1 || true
  git -C "$ROOT" worktree remove --force "$CANDIDATE_DIR" >/dev/null 2>&1 || true
  git -C "$ROOT" worktree prune >/dev/null 2>&1 || true
}
trap cleanup EXIT

rm -rf "$BASE_DIR" "$CANDIDATE_DIR" "$EVIDENCE_DIR"
mkdir -p "$EVIDENCE_DIR"

git fetch --no-tags --depth=1 origin "$BASE_SHA"
git fetch --no-tags --depth=1 origin "$CANDIDATE_SHA"
git worktree add --detach "$BASE_DIR" "$BASE_SHA"
git worktree add --detach "$CANDIDATE_DIR" "$CANDIDATE_SHA"

inject_runner() {
  local tree="$1"
  cp "$RUNNER_SOURCE" "$tree/test/SharpLink.Benchmarks/AdmissionPartitionReviewEvidence.cs"
  python3 - "$tree/test/SharpLink.Benchmarks/Program.cs" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
text = path.read_text()
needle = "    public static async Task Main(string[] args)\n    {\n"
insert = """    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(
            args[0], "--issue-245-review-evidence", StringComparison.Ordinal))
        {
            await AdmissionPartitionReviewEvidence.RunAsync(args[1..]);
            return;
        }
"""
if needle not in text:
    raise SystemExit(f"Program entrypoint shape not found in {path}")
path.write_text(text.replace(needle, insert, 1))
PY
}

inject_runner "$BASE_DIR"
inject_runner "$CANDIDATE_DIR"

dotnet restore "$BASE_DIR/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
dotnet restore "$CANDIDATE_DIR/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
dotnet build "$BASE_DIR/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release --no-restore
dotnet build "$CANDIDATE_DIR/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release --no-restore

run_one() {
  local tree="$1"
  local label="$2"
  local round="$3"
  local sha="$4"
  echo "=== issue #245 evidence: $label round $round ==="
  SHARPLINK_BENCHMARK_SHA="$sha" dotnet run \
    --project "$tree/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" \
    -c Release --no-build -- \
    --issue-245-review-evidence "$label" \
    "$EVIDENCE_DIR/${label}-${round}.json"
}

# Alternate ordering to reduce runner drift bias.
run_one "$BASE_DIR" baseline 1 "$BASE_SHA"
run_one "$CANDIDATE_DIR" candidate 1 "$CANDIDATE_SHA"
run_one "$CANDIDATE_DIR" candidate 2 "$CANDIDATE_SHA"
run_one "$BASE_DIR" baseline 2 "$BASE_SHA"
run_one "$BASE_DIR" baseline 3 "$BASE_SHA"
run_one "$CANDIDATE_DIR" candidate 3 "$CANDIDATE_SHA"

python3 - "$EVIDENCE_DIR" <<'PY'
import json
import statistics
import sys
from collections import defaultdict
from pathlib import Path

root = Path(sys.argv[1])
grouped = defaultdict(lambda: {"baseline": [], "candidate": []})
for path in sorted(root.glob("*.json")):
    doc = json.loads(path.read_text())
    label = doc["label"]
    for item in doc["results"]:
        key = (item["kind"], item["partitions"], item["concurrency"])
        grouped[key][label].append(item)

lines = [
    "# Issue #245 controlled review evidence",
    "",
    "Baseline: `c89a79bf6a3acdc24dd0f3289dbbcbe84b1ab186`  ",
    "Candidate A2: `c9142d35eb5e6326bf7681b64bfb55e3d9cfec88`",
    "",
    "Each cell is the median of 3 alternating runs in the same GitHub Actions job.",
    "",
    "| Kind | Partitions | C | Baseline QPS | A2 QPS | QPS delta | Baseline P99 us | A2 P99 us | P99 delta | A2 scans | A2 visited |",
    "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
]
failures = []
for key in sorted(grouped):
    kind, partitions, concurrency = key
    sides = grouped[key]
    if len(sides["baseline"]) != 3 or len(sides["candidate"]) != 3:
        raise SystemExit(
            f"missing runs for {key}: {len(sides['baseline'])}/{len(sides['candidate'])}")
    bq = statistics.median(x["throughputPerSecond"] for x in sides["baseline"])
    cq = statistics.median(x["throughputPerSecond"] for x in sides["candidate"])
    bp = statistics.median(x["p99Us"] for x in sides["baseline"])
    cp = statistics.median(x["p99Us"] for x in sides["candidate"])
    scans = statistics.median(x["reclaimScans"] for x in sides["candidate"])
    visited = statistics.median(x["reclaimEntriesVisited"] for x in sides["candidate"])
    qdelta = (cq / bq - 1.0) * 100.0
    pdelta = (cp / bp - 1.0) * 100.0
    lines.append(
        f"| {kind} | {partitions} | {concurrency} | {bq:.0f} | {cq:.0f} | {qdelta:+.1f}% | "
        f"{bp:.2f} | {cp:.2f} | {pdelta:+.1f}% | {scans:.0f} | {visited:.0f} |")
    if kind == "rpc-recently-idle":
        if cq < bq * 0.97:
            failures.append(f"RPC c{concurrency} throughput regressed {qdelta:.1f}%")
        if cp > bp * 1.03:
            failures.append(f"RPC c{concurrency} P99 regressed {pdelta:.1f}%")

lines.extend([
    "",
    "Gate: end-to-end partitioned unary throughput must not regress >3%; P99 must not regress >3%.",
    "Pool rows are attribution/contention evidence; expired-churn rows intentionally exercise the rare reconciliation path.",
])
summary = "\n".join(lines) + "\n"
(root / "summary.md").write_text(summary)
print(summary)
if failures:
    print("Gate failures:")
    for failure in failures:
        print(f"- {failure}")
    raise SystemExit(1)
PY
