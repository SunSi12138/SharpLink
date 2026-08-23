#!/usr/bin/env bash
set -euo pipefail

ROOT="$(pwd)"
BASE_SHA="c89a79bf6a3acdc24dd0f3289dbbcbe84b1ab186"
CANDIDATE_SHA="c9142d35eb5e6326bf7681b64bfb55e3d9cfec88"
BASE_DIR="${RUNNER_TEMP:-/tmp}/issue245-baseline"
CANDIDATE_DIR="${RUNNER_TEMP:-/tmp}/issue245-candidate"
EVIDENCE_DIR="$ROOT/artifacts/issue245-c1-diagnostic"
RUNNER_SOURCE="$ROOT/test/SharpLink.Benchmarks/AdmissionPartitionC1Diagnostic.cs"

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
  cp "$RUNNER_SOURCE" "$tree/test/SharpLink.Benchmarks/AdmissionPartitionC1Diagnostic.cs"
  python3 - "$tree/test/SharpLink.Benchmarks/Program.cs" <<'PY'
from pathlib import Path
import sys
path = Path(sys.argv[1])
text = path.read_text()
needle = "    public static async Task Main(string[] args)\n    {\n"
insert = """    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(
            args[0], "--issue-245-c1-diagnostic", StringComparison.Ordinal))
        {
            await AdmissionPartitionC1Diagnostic.RunAsync(args[1..]);
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
  local tree="$1" label="$2" round="$3" sha="$4"
  echo "=== issue #245 c1 diagnostic: $label round $round ==="
  SHARPLINK_BENCHMARK_SHA="$sha" dotnet run \
    --project "$tree/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" \
    -c Release --no-build -- \
    --issue-245-c1-diagnostic "$label" "$EVIDENCE_DIR/${label}-${round}.json"
}

# Three ABBA-style rounds are enough for diagnosis; each scenario has 20k warmup
# and ~50k+ measured operations.
run_one "$BASE_DIR" baseline 1 "$BASE_SHA"
run_one "$CANDIDATE_DIR" candidate 1 "$CANDIDATE_SHA"
run_one "$CANDIDATE_DIR" candidate 2 "$CANDIDATE_SHA"
run_one "$BASE_DIR" baseline 2 "$BASE_SHA"
run_one "$BASE_DIR" baseline 3 "$BASE_SHA"
run_one "$CANDIDATE_DIR" candidate 3 "$CANDIDATE_SHA"

python3 - "$EVIDENCE_DIR" <<'PY'
import json, statistics, sys
from collections import defaultdict
from pathlib import Path
root = Path(sys.argv[1])
g = defaultdict(lambda: {"baseline": [], "candidate": []})
for path in root.glob("*.json"):
    label = path.name.split("-")[0]
    for item in json.loads(path.read_text()):
        g[(item["kind"], item["concurrency"])][label].append(item)
print("# Issue #245 c1 diagnostic (median of 3 same-runner alternating runs)")
print("| Kind | C | Baseline QPS | A2 QPS | QPS delta | Baseline P99 us | A2 P99 us | P99 delta | Base CPU us/op | A2 CPU us/op |")
print("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
for key in sorted(g):
    kind, c = key
    b, a = g[key]["baseline"], g[key]["candidate"]
    bq = statistics.median(x["qps"] for x in b); aq = statistics.median(x["qps"] for x in a)
    bp = statistics.median(x["p99Us"] for x in b); ap = statistics.median(x["p99Us"] for x in a)
    bc = statistics.median(x["cpuUsPerOperation"] for x in b); ac = statistics.median(x["cpuUsPerOperation"] for x in a)
    print(f"| {kind} | {c} | {bq:.0f} | {aq:.0f} | {(aq/bq-1)*100:+.1f}% | {bp:.2f} | {ap:.2f} | {(ap/bp-1)*100:+.1f}% | {bc:.2f} | {ac:.2f} |")
PY
