#!/usr/bin/env bash
set -euo pipefail

ROOT="$(pwd)"
BASE_SHA="c89a79bf6a3acdc24dd0f3289dbbcbe84b1ab186"
CANDIDATE_SHA="c9142d35eb5e6326bf7681b64bfb55e3d9cfec88"
BASE_DIR="${RUNNER_TEMP:-/tmp}/issue245-baseline"
CANDIDATE_DIR="${RUNNER_TEMP:-/tmp}/issue245-candidate"
DELAY1_DIR="${RUNNER_TEMP:-/tmp}/issue245-candidate-delay1"
DELAY2_DIR="${RUNNER_TEMP:-/tmp}/issue245-candidate-delay2"
DELAY4_DIR="${RUNNER_TEMP:-/tmp}/issue245-candidate-delay4"
EVIDENCE_DIR="$ROOT/artifacts/issue245-c1-pacing-diagnostic"
RPC_SOURCE="$ROOT/test/SharpLink.Benchmarks/AdmissionPartitionRpcEvidence.cs"

cleanup() {
  for tree in "$BASE_DIR" "$CANDIDATE_DIR" "$DELAY1_DIR" "$DELAY2_DIR" "$DELAY4_DIR"; do
    git -C "$ROOT" worktree remove --force "$tree" >/dev/null 2>&1 || true
  done
  git -C "$ROOT" worktree prune >/dev/null 2>&1 || true
}
trap cleanup EXIT

rm -rf "$BASE_DIR" "$CANDIDATE_DIR" "$DELAY1_DIR" "$DELAY2_DIR" "$DELAY4_DIR" "$EVIDENCE_DIR"
mkdir -p "$EVIDENCE_DIR"

git fetch --no-tags --depth=1 origin "$BASE_SHA"
git fetch --no-tags --depth=1 origin "$CANDIDATE_SHA"
git worktree add --detach "$BASE_DIR" "$BASE_SHA"
git worktree add --detach "$CANDIDATE_DIR" "$CANDIDATE_SHA"
git worktree add --detach "$DELAY1_DIR" "$CANDIDATE_SHA"
git worktree add --detach "$DELAY2_DIR" "$CANDIDATE_SHA"
git worktree add --detach "$DELAY4_DIR" "$CANDIDATE_SHA"

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

inject_release_delay() {
  local tree="$1" delay_us="$2"
  python3 - "$tree/src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs" "$delay_us" <<'PY'
from pathlib import Path
import sys
path = Path(sys.argv[1])
delay_us = int(sys.argv[2])
text = path.read_text()
needle = '''        DisposeRules(evicted);
    }

    private List<AdmissionRuleRuntime>? ReclaimIdleEntriesIfDue(long now)
'''
replacement = f'''        DisposeRules(evicted);
        Issue245DiagnosticDelay();
    }}

    private static void Issue245DiagnosticDelay()
    {{
        const long delayMicroseconds = {delay_us}L;
        var delayTicks = Math.Max(
            1L,
            (System.Diagnostics.Stopwatch.Frequency * delayMicroseconds + 999_999L) / 1_000_000L);
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + delayTicks;
        while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
            System.Threading.Thread.SpinWait(16);
    }}

    private List<AdmissionRuleRuntime>? ReclaimIdleEntriesIfDue(long now)
'''
if needle not in text:
    raise SystemExit(f"A2 Release shape not found in {path}")
path.write_text(text.replace(needle, replacement, 1))
PY
}

for tree in "$BASE_DIR" "$CANDIDATE_DIR" "$DELAY1_DIR" "$DELAY2_DIR" "$DELAY4_DIR"; do
  inject_runner "$tree"
done
inject_release_delay "$DELAY1_DIR" 1
inject_release_delay "$DELAY2_DIR" 2
inject_release_delay "$DELAY4_DIR" 4

for tree in "$BASE_DIR" "$CANDIDATE_DIR" "$DELAY1_DIR" "$DELAY2_DIR" "$DELAY4_DIR"; do
  dotnet restore "$tree/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
  dotnet build "$tree/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release --no-restore
 done

base_dll="$BASE_DIR/test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"
candidate_dll="$CANDIDATE_DIR/test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"
delay1_dll="$DELAY1_DIR/test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"
delay2_dll="$DELAY2_DIR/test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"
delay4_dll="$DELAY4_DIR/test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"

run_rpc() {
  local dll="$1" label="$2" partitions="$3" round="$4" sha="$5" file_prefix="$6"
  echo "=== issue #245 c1 pacing diagnostic: p$partitions $label round $round ==="
  SHARPLINK_BENCHMARK_SHA="$sha" dotnet "$dll" \
    --issue-245-rpc-evidence "$label" 1 \
    "$EVIDENCE_DIR/${file_prefix}-p${partitions}-${round}.json" default "$partitions"
}

# First establish whether the c1 effect scales with the old O(partition-count) Release scan.
for partitions in 1 16 128 1024; do
  run_rpc "$base_dll" baseline "$partitions" 1 "$BASE_SHA" sweep-baseline
  run_rpc "$candidate_dll" candidate "$partitions" 1 "$CANDIDATE_SHA" sweep-candidate
  run_rpc "$candidate_dll" candidate "$partitions" 2 "$CANDIDATE_SHA" sweep-candidate
  run_rpc "$base_dll" baseline "$partitions" 2 "$BASE_SHA" sweep-baseline
  run_rpc "$base_dll" baseline "$partitions" 3 "$BASE_SHA" sweep-baseline
  run_rpc "$candidate_dll" candidate "$partitions" 3 "$CANDIDATE_SHA" sweep-candidate
done

# Then add controlled post-Release busy time only to A2 at p1024. The p1024 baseline and
# zero-delay A2 samples above are reused, so this directly tests the pacing hypothesis.
for round in 1 2 3; do
  run_rpc "$delay1_dll" candidate-delay-1us 1024 "$round" "$CANDIDATE_SHA" delay1
  run_rpc "$delay2_dll" candidate-delay-2us 1024 "$round" "$CANDIDATE_SHA" delay2
  run_rpc "$delay4_dll" candidate-delay-4us 1024 "$round" "$CANDIDATE_SHA" delay4
done

python3 - "$EVIDENCE_DIR" <<'PY'
import json
import statistics
import sys
from collections import defaultdict
from pathlib import Path

root = Path(sys.argv[1])
sweep = defaultdict(lambda: {"baseline": [], "candidate": []})
delays = defaultdict(list)

for path in sorted(root.glob("*.json")):
    item = json.loads(path.read_text())
    if path.name.startswith("sweep-"):
        sweep[item["partitions"]][item["label"]].append(item)
    elif path.name.startswith("delay"):
        delays[item["label"]].append(item)

def med(items, key):
    return statistics.median(x[key] for x in items)

lines = [
    "# Issue #245 c1 pacing diagnostic",
    "",
    "Fresh process per sample; 20k RPC warmup plus a minimum 5 second maturation window; median of three runs.",
    "",
    "## Partition-count sweep",
    "",
    "| Partitions | Base QPS | A2 QPS | QPS delta | Base P99 us | A2 P99 us | P99 delta | Base CPU us/op | A2 CPU us/op |",
    "|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
]
for partitions in (1, 16, 128, 1024):
    sides = sweep[partitions]
    if len(sides["baseline"]) != 3 or len(sides["candidate"]) != 3:
        raise SystemExit(f"missing sweep runs for p{partitions}")
    bq, aq = med(sides["baseline"], "throughputPerSecond"), med(sides["candidate"], "throughputPerSecond")
    bp, ap = med(sides["baseline"], "p99Us"), med(sides["candidate"], "p99Us")
    bc, ac = med(sides["baseline"], "cpuUsPerOperation"), med(sides["candidate"], "cpuUsPerOperation")
    lines.append(
        f"| {partitions} | {bq:.0f} | {aq:.0f} | {(aq/bq-1)*100:+.1f}% | "
        f"{bp:.2f} | {ap:.2f} | {(ap/bp-1)*100:+.1f}% | {bc:.2f} | {ac:.2f} |")

base1024 = sweep[1024]["baseline"]
base_q = med(base1024, "throughputPerSecond")
base_p = med(base1024, "p99Us")
base_c = med(base1024, "cpuUsPerOperation")
zero = sweep[1024]["candidate"]
delay_rows = [("0 us", zero)]
for label, display in (("candidate-delay-1us", "1 us"), ("candidate-delay-2us", "2 us"), ("candidate-delay-4us", "4 us")):
    items = delays[label]
    if len(items) != 3:
        raise SystemExit(f"missing delay runs for {label}")
    delay_rows.append((display, items))

lines.extend([
    "",
    "## A2 post-Release delay sweep at 1024 partitions",
    "",
    f"Reference baseline median: {base_q:.0f} QPS, {base_p:.2f} us P99, {base_c:.2f} CPU us/op.",
    "",
    "| Added delay | A2 QPS | vs baseline | A2 P99 us | vs baseline | A2 CPU us/op |",
    "|---:|---:|---:|---:|---:|---:|",
])
for display, items in delay_rows:
    q = med(items, "throughputPerSecond")
    p = med(items, "p99Us")
    c = med(items, "cpuUsPerOperation")
    lines.append(
        f"| {display} | {q:.0f} | {(q/base_q-1)*100:+.1f}% | {p:.2f} | {(p/base_p-1)*100:+.1f}% | {c:.2f} |")

summary = "\n".join(lines) + "\n"
(root / "summary.md").write_text(summary)
print(summary)
PY
