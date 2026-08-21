#!/usr/bin/env bash
set -euo pipefail

ROOT="$GITHUB_WORKSPACE"
CAND="$RUNNER_TEMP/issue252-heap-candidate"
OUT="$RUNNER_TEMP/issue252-heap-results"
rm -rf "$CAND" "$OUT"
mkdir -p "$OUT"

git fetch --no-tags origin agent/issue-252-pending-segments
git worktree add --detach "$CAND" origin/agent/issue-252-pending-segments
trap 'git -C "$ROOT" worktree remove --force "$CAND" >/dev/null 2>&1 || true' EXIT

echo "[issue252-heap] candidate_sha=$(git -C "$CAND" rev-parse HEAD)"

dotnet tool install --global dotnet-gcdump >/dev/null 2>&1 || dotnet tool update --global dotnet-gcdump >/dev/null
export PATH="$PATH:$HOME/.dotnet/tools"

dotnet restore "$CAND/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" >/dev/null
dotnet build "$CAND/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release --no-restore -v minimal >/dev/null

capture() {
  local label="$1"
  local active="$2"
  local log="$OUT/$label.log"
  local dump="$OUT/$label.gcdump"
  local report="$OUT/$label.report.txt"

  dotnet run --project "$CAND/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" \
    -c Release --no-build -- \
    --pending-request-segmentation-evidence heap-hold \
    --active "$active" --connections 100 --hold-seconds 30 >"$log" 2>&1 &
  local runner_pid=$!

  local pid=""
  for _ in $(seq 1 200); do
    if grep -q '"processId"' "$log" 2>/dev/null; then
      pid=$(python3 - "$log" <<'PY'
import json, sys
for line in open(sys.argv[1], encoding='utf-8'):
    line=line.strip()
    if line.startswith('{') and '"processId"' in line:
        print(json.loads(line)['processId'])
        break
PY
)
      break
    fi
    sleep 0.1
  done
  if [[ -z "$pid" ]]; then
    cat "$log"
    echo "[issue252-heap] failed to discover process id for $label" >&2
    kill "$runner_pid" 2>/dev/null || true
    exit 1
  fi

  echo "[issue252-heap] collecting label=$label active=$active pid=$pid"
  dotnet-gcdump collect -p "$pid" -o "$dump" >/dev/null
  dotnet-gcdump report "$dump" > "$report"
  cat "$log"
  echo "[issue252-heap] report label=$label"
  grep -E 'PendingRequestTable|PendingCall|System.Int32\[\]|System.Object' "$report" || true

  kill "$runner_pid" 2>/dev/null || true
  wait "$runner_pid" 2>/dev/null || true
}

capture c2-idle 0
capture c2-active1 1

echo "[issue252-heap] completed"
