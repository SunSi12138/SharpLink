#!/usr/bin/env bash
set -u

root="$(cd "$(dirname "$0")" && pwd)"
out="$root/evidence-132"
mkdir -p "$out"

warmup=2000
seconds=4
max_ops=200000
rounds=5

export DOTNET_CLI_HOME="$root/.dotnet-cli"
export SHARPLINK_BENCHMARK_SHA="$(git -C "$root" rev-parse HEAD 2>/dev/null || echo unknown)"

failures=0

run_one() {
  local component="$1"
  local scenario="$2"
  local round="$3"
  local json="$out/${component}-${scenario}-r${round}.json"
  taskset -c 5 dotnet run -c Release --no-build \
    --project "$root/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -- \
    --feature-evidence "$component" "$scenario" "$warmup" "$seconds" "$max_ops" "$json" \
    >/dev/null 2>&1
  if [ $? -eq 0 ]; then
    python3 - "$json" "$component" "$scenario" <<'PY'
import json, sys
p, c, s = sys.argv[1], sys.argv[2], sys.argv[3]
d = json.load(open(p))
print(f"OK {c}/{s} qps={d['throughputPerSecond']:.0f} cpuUsPerOp={d['cpuUsPerOperation']:.2f} allocBPerOp={d['allocatedBytesPerOperation']:.0f}")
PY
  else
    echo "FAIL $component/$scenario r$round"
    failures=$((failures + 1))
  fi
}

for round in $(seq 1 "$rounds"); do
  run_one client FixedDefault "$round"
  run_one client ClientInterceptor "$round"
  run_one server StaticDefault "$round"
  run_one server ServerInterceptor "$round"
done

echo "DONE"
if [ "$failures" -ne 0 ]; then
  echo "FAILED_RUNS=$failures"
  exit 1
fi
