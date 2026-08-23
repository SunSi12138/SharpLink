#!/usr/bin/env bash
set -euo pipefail

ROOT="$(pwd)"
BASE_SHA="c89a79bf6a3acdc24dd0f3289dbbcbe84b1ab186"
CANDIDATE_SHA="c9142d35eb5e6326bf7681b64bfb55e3d9cfec88"
BASE_DIR="${RUNNER_TEMP:-/tmp}/issue245-baseline"
CANDIDATE_DIR="${RUNNER_TEMP:-/tmp}/issue245-candidate"
EVIDENCE_DIR="$ROOT/artifacts/issue245-c1-flush-diagnostic"
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

inject_runner() {
  local tree="$1"
  cp "$RPC_SOURCE" "$tree/test/SharpLink.Benchmarks/AdmissionPartitionRpcEvidence.cs"
  python3 - "$tree/test/SharpLink.Benchmarks/Program.cs" <<'PY'
from pathlib import Path
import sys
path = Path(sys.argv[1])
text = path.read_text()
needle = "    public static async Task Main(string[] args)\n    {\n"
insert = """    public static async Task Main(string[] args)
    {
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

inject_runner "$BASE_DIR"
inject_runner "$CANDIDATE_DIR"
dotnet restore "$BASE_DIR/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
dotnet restore "$CANDIDATE_DIR/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
dotnet build "$BASE_DIR/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release --no-restore
dotnet build "$CANDIDATE_DIR/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release --no-restore

base_dll="$BASE_DIR/test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"
candidate_dll="$CANDIDATE_DIR/test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"

run_rpc() {
  local dll="$1" label="$2" mode="$3" round="$4" sha="$5"
  echo "=== issue #245 c1 flush diagnostic: $mode $label round $round ==="
  SHARPLINK_BENCHMARK_SHA="$sha" dotnet "$dll" \
    --issue-245-rpc-evidence "$label" 1 \
    "$EVIDENCE_DIR/${mode}-${label}-${round}.json" "$mode"
}

for mode in default server-low-latency client-low-latency both-low-latency; do
  run_rpc "$base_dll" baseline "$mode" 1 "$BASE_SHA"
  run_rpc "$candidate_dll" candidate "$mode" 1 "$CANDIDATE_SHA"
  run_rpc "$candidate_dll" candidate "$mode" 2 "$CANDIDATE_SHA"
  run_rpc "$base_dll" baseline "$mode" 2 "$BASE_SHA"
  run_rpc "$base_dll" baseline "$mode" 3 "$BASE_SHA"
  run_rpc "$candidate_dll" candidate "$mode" 3 "$CANDIDATE_SHA"
done

python3 - "$EVIDENCE_DIR" <<'PY'
import json
import statistics
import sys
from collections import defaultdict
from pathlib import Path

root = Path(sys.argv[1])
rows = defaultdict(lambda: {"baseline": [], "candidate": []})
for path in sorted(root.glob("*.json")):
    item = json.loads(path.read_text())
    rows[item["mode"]][item["label"]].append(item)

lines = [
    "# Issue #245 c1 flush diagnostic",
    "",
    "Fresh process per sample; 20k RPC warmup plus a minimum 5 second maturation window; median of three alternating runs.",
    "",
    "| Mode | Base QPS | A2 QPS | QPS delta | Base P99 us | A2 P99 us | P99 delta | Base CPU us/op | A2 CPU us/op | Base B/op | A2 B/op |",
    "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
]
for mode in ("default", "server-low-latency", "client-low-latency", "both-low-latency"):
    sides = rows[mode]
    if len(sides["baseline"]) != 3 or len(sides["candidate"]) != 3:
        raise SystemExit(f"missing runs for {mode}")
    def med(side, key):
        return statistics.median(x[key] for x in sides[side])
    bq, aq = med("baseline", "throughputPerSecond"), med("candidate", "throughputPerSecond")
    bp, ap = med("baseline", "p99Us"), med("candidate", "p99Us")
    bc, ac = med("baseline", "cpuUsPerOperation"), med("candidate", "cpuUsPerOperation")
    ba, aa = med("baseline", "allocatedBytesPerOperation"), med("candidate", "allocatedBytesPerOperation")
    lines.append(
        f"| {mode} | {bq:.0f} | {aq:.0f} | {(aq/bq-1)*100:+.1f}% | "
        f"{bp:.2f} | {ap:.2f} | {(ap/bp-1)*100:+.1f}% | "
        f"{bc:.2f} | {ac:.2f} | {ba:.1f} | {aa:.1f} |")
summary = "\n".join(lines) + "\n"
(root / "summary.md").write_text(summary)
print(summary)
PY
