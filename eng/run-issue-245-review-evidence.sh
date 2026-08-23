#!/usr/bin/env bash
set -euo pipefail

ROOT="$(pwd)"
BASE_SHA="c89a79bf6a3acdc24dd0f3289dbbcbe84b1ab186"
CANDIDATE_SHA="c9142d35eb5e6326bf7681b64bfb55e3d9cfec88"
BASE_DIR="${RUNNER_TEMP:-/tmp}/issue245-baseline"
CANDIDATE_DIR="${RUNNER_TEMP:-/tmp}/issue245-candidate"
EVIDENCE_DIR="$ROOT/artifacts/issue245-final-evidence"
POOL_SOURCE="$ROOT/test/SharpLink.Benchmarks/AdmissionPartitionReviewEvidence.cs"
RPC_SOURCE="$ROOT/test/SharpLink.Benchmarks/AdmissionPartitionRpcEvidence.cs"

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

inject_runners() {
  local tree="$1"
  cp "$POOL_SOURCE" "$tree/test/SharpLink.Benchmarks/AdmissionPartitionReviewEvidence.cs"
  cp "$RPC_SOURCE" "$tree/test/SharpLink.Benchmarks/AdmissionPartitionRpcEvidence.cs"

  # The historical review runner also contained RPC rows. Remove that call from
  # the injected copy so pool evidence and RPC evidence never share a process.
  python3 - "$tree/test/SharpLink.Benchmarks/AdmissionPartitionReviewEvidence.cs" <<'PY'
from pathlib import Path
import sys
path = Path(sys.argv[1])
text = path.read_text()
block = '''        foreach (var concurrency in new[] { 1, 32, 128 })
        {
            results.Add(await RunRpcAsync(label, partitions: 1024, concurrency)
                .ConfigureAwait(false));
        }

'''
if block not in text:
    raise SystemExit(f"RPC loop shape not found in {path}")
path.write_text(text.replace(block, "", 1))
PY

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
        if (args.Length > 0 && string.Equals(
            args[0], "--issue-245-rpc-evidence", StringComparison.Ordinal))
        {
            await AdmissionPartitionRpcEvidence.RunAsync(args[1..]);
            return;
        }
"""
if needle not in text:
    raise SystemExit(f"Program entrypoint shape not found in {path}")
path.write_text(text.replace(needle, insert, 1))
PY
}

inject_runners "$BASE_DIR"
inject_runners "$CANDIDATE_DIR"
dotnet restore "$BASE_DIR/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
dotnet restore "$CANDIDATE_DIR/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
dotnet build "$BASE_DIR/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release --no-restore
dotnet build "$CANDIDATE_DIR/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release --no-restore

base_dll="$BASE_DIR/test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"
candidate_dll="$CANDIDATE_DIR/test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"

run_pool() {
  local dll="$1" label="$2" round="$3" sha="$4"
  echo "=== issue #245 final pool evidence: $label round $round ==="
  SHARPLINK_BENCHMARK_SHA="$sha" dotnet "$dll" \
    --issue-245-review-evidence "$label" "$EVIDENCE_DIR/pool-${label}-${round}.json"
}

run_rpc() {
  local dll="$1" label="$2" concurrency="$3" round="$4" sha="$5"
  echo "=== issue #245 isolated RPC evidence: c$concurrency $label round $round ==="
  SHARPLINK_BENCHMARK_SHA="$sha" dotnet "$dll" \
    --issue-245-rpc-evidence "$label" "$concurrency" \
    "$EVIDENCE_DIR/rpc-c${concurrency}-${label}-${round}.json"
}

# Pool evidence: five same-runner alternating processes per side.
run_pool "$base_dll" baseline 1 "$BASE_SHA"
run_pool "$candidate_dll" candidate 1 "$CANDIDATE_SHA"
run_pool "$candidate_dll" candidate 2 "$CANDIDATE_SHA"
run_pool "$base_dll" baseline 2 "$BASE_SHA"
run_pool "$base_dll" baseline 3 "$BASE_SHA"
run_pool "$candidate_dll" candidate 3 "$CANDIDATE_SHA"
run_pool "$candidate_dll" candidate 4 "$CANDIDATE_SHA"
run_pool "$base_dll" baseline 4 "$BASE_SHA"
run_pool "$base_dll" baseline 5 "$BASE_SHA"
run_pool "$candidate_dll" candidate 5 "$CANDIDATE_SHA"

# RPC merge gate: every cell gets a fresh process with identical RPC-only warmup.
# Alternate which side runs first to reduce runner-frequency/time drift bias.
for concurrency in 1 32 128; do
  run_rpc "$base_dll" baseline "$concurrency" 1 "$BASE_SHA"
  run_rpc "$candidate_dll" candidate "$concurrency" 1 "$CANDIDATE_SHA"
  run_rpc "$candidate_dll" candidate "$concurrency" 2 "$CANDIDATE_SHA"
  run_rpc "$base_dll" baseline "$concurrency" 2 "$BASE_SHA"
  run_rpc "$base_dll" baseline "$concurrency" 3 "$BASE_SHA"
  run_rpc "$candidate_dll" candidate "$concurrency" 3 "$CANDIDATE_SHA"
  run_rpc "$candidate_dll" candidate "$concurrency" 4 "$CANDIDATE_SHA"
  run_rpc "$base_dll" baseline "$concurrency" 4 "$BASE_SHA"
  run_rpc "$base_dll" baseline "$concurrency" 5 "$BASE_SHA"
  run_rpc "$candidate_dll" candidate "$concurrency" 5 "$CANDIDATE_SHA"
done

python3 - "$EVIDENCE_DIR" <<'PY'
import json
import statistics
import sys
from collections import defaultdict
from pathlib import Path

root = Path(sys.argv[1])
pool = defaultdict(lambda: {"baseline": [], "candidate": []})
rpc = defaultdict(lambda: {"baseline": [], "candidate": []})

for path in sorted(root.glob("pool-*.json")):
    doc = json.loads(path.read_text())
    label = doc["label"]
    for item in doc["results"]:
        if not item["kind"].startswith("pool-"):
            continue
        key = (item["kind"], item["partitions"], item["concurrency"])
        pool[key][label].append(item)

for path in sorted(root.glob("rpc-*.json")):
    item = json.loads(path.read_text())
    rpc[item["concurrency"]][item["label"]].append(item)

failures = []
lines = [
    "# Issue #245 final controlled evidence",
    "",
    "Baseline: `c89a79bf6a3acdc24dd0f3289dbbcbe84b1ab186`  ",
    "Candidate A2: `c9142d35eb5e6326bf7681b64bfb55e3d9cfec88`",
    "",
    "Pool and RPC measurements run in separate processes. Each RPC concurrency cell also runs in a fresh process with identical 20k RPC-only warmup. Every reported cell is the median of five alternating same-runner runs.",
    "",
    "## Pool",
    "",
    "| Kind | Partitions | C | Baseline QPS | A2 QPS | QPS delta | Baseline P99 us | A2 P99 us | P99 delta | Base CPU us/op | A2 CPU us/op | Base B/op | A2 B/op | A2 scans | A2 visited |",
    "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
]

for key in sorted(pool):
    kind, partitions, concurrency = key
    sides = pool[key]
    if len(sides["baseline"]) != 5 or len(sides["candidate"]) != 5:
        raise SystemExit(f"missing pool runs for {key}")
    bq = statistics.median(x["throughputPerSecond"] for x in sides["baseline"])
    aq = statistics.median(x["throughputPerSecond"] for x in sides["candidate"])
    bp = statistics.median(x["p99Us"] for x in sides["baseline"])
    ap = statistics.median(x["p99Us"] for x in sides["candidate"])
    bc = statistics.median(x["cpuUsPerOperation"] for x in sides["baseline"])
    ac = statistics.median(x["cpuUsPerOperation"] for x in sides["candidate"])
    ba = statistics.median(x["allocatedBytesPerOperation"] for x in sides["baseline"])
    aa = statistics.median(x["allocatedBytesPerOperation"] for x in sides["candidate"])
    scans = statistics.median(x["reclaimScans"] for x in sides["candidate"])
    visited = statistics.median(x["reclaimEntriesVisited"] for x in sides["candidate"])
    qdelta = (aq / bq - 1.0) * 100.0
    pdelta = (ap / bp - 1.0) * 100.0
    lines.append(
        f"| {kind} | {partitions} | {concurrency} | {bq:.0f} | {aq:.0f} | {qdelta:+.1f}% | "
        f"{bp:.2f} | {ap:.2f} | {pdelta:+.1f}% | {bc:.2f} | {ac:.2f} | "
        f"{ba:.1f} | {aa:.1f} | {scans:.0f} | {visited:.0f} |")

    if kind in ("pool-recently-idle", "pool-active-peers") and (scans != 0 or visited != 0):
        failures.append(
            f"{kind} partitions={partitions} c={concurrency} performed normal-path reclaim scan")
    if kind == "pool-recently-idle" and partitions == 1 and concurrency == 1:
        if ac > bc * 1.02:
            failures.append(f"single-partition pool CPU regressed {(ac/bc-1)*100:.1f}%")
        if aa > ba + 1.0:
            failures.append(f"single-partition pool allocation increased {aa-ba:.1f} B/op")

lines.extend([
    "",
    "## End-to-end partitioned unary RPC",
    "",
    "| C | Baseline QPS | A2 QPS | QPS delta | Baseline P99 us | A2 P99 us | P99 delta | Base CPU us/op | A2 CPU us/op | Base B/op | A2 B/op |",
    "|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
])

for concurrency in (1, 32, 128):
    sides = rpc[concurrency]
    if len(sides["baseline"]) != 5 or len(sides["candidate"]) != 5:
        raise SystemExit(f"missing RPC runs for c{concurrency}")
    bq = statistics.median(x["throughputPerSecond"] for x in sides["baseline"])
    aq = statistics.median(x["throughputPerSecond"] for x in sides["candidate"])
    bp = statistics.median(x["p99Us"] for x in sides["baseline"])
    ap = statistics.median(x["p99Us"] for x in sides["candidate"])
    bc = statistics.median(x["cpuUsPerOperation"] for x in sides["baseline"])
    ac = statistics.median(x["cpuUsPerOperation"] for x in sides["candidate"])
    ba = statistics.median(x["allocatedBytesPerOperation"] for x in sides["baseline"])
    aa = statistics.median(x["allocatedBytesPerOperation"] for x in sides["candidate"])
    qdelta = (aq / bq - 1.0) * 100.0
    pdelta = (ap / bp - 1.0) * 100.0
    lines.append(
        f"| {concurrency} | {bq:.0f} | {aq:.0f} | {qdelta:+.1f}% | {bp:.2f} | {ap:.2f} | "
        f"{pdelta:+.1f}% | {bc:.2f} | {ac:.2f} | {ba:.1f} | {aa:.1f} |")
    if aq < bq * 0.97:
        failures.append(f"RPC c{concurrency} throughput regressed {qdelta:.1f}%")
    if ap > bp * 1.03:
        failures.append(f"RPC c{concurrency} P99 regressed {pdelta:.1f}%")
    if aa > ba + 1.0:
        failures.append(f"RPC c{concurrency} allocation increased {aa-ba:.1f} B/op")

lines.extend([
    "",
    "Merge gates: normal no-expired path performs zero reclaim scans; single-partition pool CPU does not regress >2%; no new per-request allocation; end-to-end partitioned unary throughput/P99 do not regress >3%.",
])

summary = "\n".join(lines) + "\n"
(root / "summary.md").write_text(summary)
print(summary)
if failures:
    print("Gate failures:")
    for failure in failures:
        print(f"- {failure}")
    raise SystemExit(1)
print("All issue #245 final evidence gates passed.")
PY
