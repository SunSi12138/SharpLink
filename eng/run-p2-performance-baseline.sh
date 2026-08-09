#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TIMESTAMP="$(date -u +%Y%m%d-%H%M%S)"
OUTPUT_ROOT="${1:-${SHARPLINK_P2_BASELINE_OUTPUT:-$ROOT/artifacts/p2-performance-baseline/$TIMESTAMP}}"
RUNS="${SHARPLINK_P2_BASELINE_RUNS:-5}"
WARMUP_OPERATIONS="${SHARPLINK_P2_BASELINE_WARMUP_OPERATIONS:-2000}"
MEASUREMENT_SECONDS="${SHARPLINK_P2_BASELINE_MEASUREMENT_SECONDS:-3}"
MAX_OPERATIONS="${SHARPLINK_P2_BASELINE_MAX_OPERATIONS:-2000000}"
BDN_JOB="${SHARPLINK_P2_BASELINE_BDN_JOB:-Short}"
BDN_LAUNCH_COUNT="${SHARPLINK_P2_BASELINE_BDN_LAUNCH_COUNT:-5}"
RUN_LEGACY="${SHARPLINK_P2_BASELINE_RUN_LEGACY:-1}"
SKIP_BUILD="${SHARPLINK_P2_BASELINE_SKIP_BUILD:-0}"
BENCHMARK_SHA="${SHARPLINK_BENCHMARK_SHA:-}"

if [[ -z "$BENCHMARK_SHA" ]] && command -v git >/dev/null 2>&1 && [[ -d "$ROOT/.git" ]]; then
  BENCHMARK_SHA="$(git -C "$ROOT" rev-parse HEAD)"
fi
if [[ -z "$BENCHMARK_SHA" ]]; then
  echo "SHARPLINK_BENCHMARK_SHA is required when the source tree has no Git metadata." >&2
  exit 2
fi
if [[ ! "$RUNS" =~ ^[1-9][0-9]*$ ]] ||
   [[ ! "$WARMUP_OPERATIONS" =~ ^[0-9]+$ ]] ||
   [[ ! "$MAX_OPERATIONS" =~ ^[1-9][0-9]*$ ]] ||
   [[ ! "$BDN_LAUNCH_COUNT" =~ ^[1-9][0-9]*$ ]]; then
  echo "Run counts, warmup operations, max operations, and launch count must be integers in range." >&2
  exit 2
fi
if [[ "$RUN_LEGACY" != "0" && "$RUN_LEGACY" != "1" ]] ||
   [[ "$SKIP_BUILD" != "0" && "$SKIP_BUILD" != "1" ]]; then
  echo "SHARPLINK_P2_BASELINE_RUN_LEGACY and SHARPLINK_P2_BASELINE_SKIP_BUILD must be 0 or 1." >&2
  exit 2
fi

PROJECT="test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
FEATURE_ROOT="$OUTPUT_ROOT/feature"
ENVIRONMENT_ROOT="$OUTPUT_ROOT/environment"
JIT_ROOT="$OUTPUT_ROOT/jit"
REPORT_ROOT="$OUTPUT_ROOT/report"
mkdir -p "$FEATURE_ROOT" "$ENVIRONMENT_ROOT" "$JIT_ROOT" "$REPORT_ROOT"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_TieredCompilation="${DOTNET_TieredCompilation:-1}"
export DOTNET_TieredPGO="${DOTNET_TieredPGO:-1}"
export SHARPLINK_BENCHMARK_SHA="$BENCHMARK_SHA"

{
  printf 'timestamp_utc=%s\n' "$(date -u --iso-8601=seconds)"
  printf 'benchmark_sha=%s\n' "$BENCHMARK_SHA"
  printf 'output_root=%s\n' "$OUTPUT_ROOT"
  printf 'runs=%s\n' "$RUNS"
  printf 'warmup_operations=%s\n' "$WARMUP_OPERATIONS"
  printf 'measurement_seconds=%s\n' "$MEASUREMENT_SECONDS"
  printf 'max_operations=%s\n' "$MAX_OPERATIONS"
  printf 'bdn_job=%s\n' "$BDN_JOB"
  printf 'bdn_launch_count=%s\n' "$BDN_LAUNCH_COUNT"
  printf 'tiered_compilation=%s\n' "$DOTNET_TieredCompilation"
  printf 'tiered_pgo=%s\n' "$DOTNET_TieredPGO"
  uname -a
  lscpu
  free -h
  dotnet --info
} > "$ENVIRONMENT_ROOT/fingerprint.txt"

if [[ -r /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor ]]; then
  cp /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor "$ENVIRONMENT_ROOT/cpu-scaling-governor.txt"
fi
if command -v sysctl >/dev/null 2>&1; then
  sysctl kernel.perf_event_paranoid > "$ENVIRONMENT_ROOT/perf-event-policy.txt" 2>&1 || true
fi

cd "$ROOT"
if [[ "$SKIP_BUILD" == "0" ]]; then
  dotnet build "$PROJECT" -c Release -v minimal > "$OUTPUT_ROOT/build.log"
fi

SERVER_SCENARIOS=(
  StaticDefault
  AdmissionImmediate
  ServerInterceptor
  MetricsClientAndServer
  ServerTraceOnePercent
  ServerTraceAll
  DynamicRegisteredStaticHit
  DynamicServiceActual
)
CLIENT_SCENARIOS=(
  FixedDefault
  StaticTwoEndpoints
  StaticFourEndpoints
  StaticSixteenEndpoints
  DynamicFourEndpoints
  RetryFirstSuccess
  AlwaysAcceptAdmission
  ClosedCircuitBreaker
  ClientInterceptor
  MetricsClientAndServer
  ClientTraceOnePercent
  ClientTraceAll
)

run_feature_scenario() {
  local component="$1"
  local scenario="$2"
  local repetition="$3"
  local output="$FEATURE_ROOT/r$(printf '%02d' "$repetition")/$component-$scenario.json"
  mkdir -p "$(dirname "$output")"
  dotnet run -c Release --no-build --project "$PROJECT" -- \
    --feature-evidence "$component" "$scenario" \
    "$WARMUP_OPERATIONS" "$MEASUREMENT_SECONDS" "$MAX_OPERATIONS" "$output" \
    > "$output.stdout"
}

for repetition in $(seq 1 "$RUNS"); do
  if (( repetition % 2 == 1 )); then
    for scenario in "${SERVER_SCENARIOS[@]}"; do
      run_feature_scenario server "$scenario" "$repetition"
    done
    for scenario in "${CLIENT_SCENARIOS[@]}"; do
      run_feature_scenario client "$scenario" "$repetition"
    done
  else
    for ((index=${#CLIENT_SCENARIOS[@]} - 1; index >= 0; index--)); do
      run_feature_scenario client "${CLIENT_SCENARIOS[index]}" "$repetition"
    done
    for ((index=${#SERVER_SCENARIOS[@]} - 1; index >= 0; index--)); do
      run_feature_scenario server "${SERVER_SCENARIOS[index]}" "$repetition"
    done
  fi
done

dotnet run -c Release --no-build --project "$PROJECT" -- \
  --layout-evidence "$REPORT_ROOT/layout-evidence.json"

dotnet run -c Release --no-build --project "$PROJECT" -- \
  --summarize-feature-evidence "$FEATURE_ROOT" \
  "$REPORT_ROOT/feature-baseline.md" \
  "$REPORT_ROOT/feature-baseline.jsonl"

run_bdn() {
  local filter="$1"
  local name="$2"
  shift 2
  dotnet run -c Release --no-build --project "$PROJECT" -- \
    --filter "$filter" \
    "$@" \
    --artifacts "$OUTPUT_ROOT/$name" \
    --noOverwrite \
    > "$OUTPUT_ROOT/$name.log"
}

run_bdn '*FeatureMatrixBenchmarks*' bdn-feature \
  --job "$BDN_JOB" --launchCount "$BDN_LAUNCH_COUNT"
run_bdn '*FrameMixParserBenchmarks*' bdn-frame-mix \
  --job "$BDN_JOB" --launchCount "$BDN_LAUNCH_COUNT"

if [[ "$RUN_LEGACY" == "1" ]]; then
  run_bdn '*UnaryBenchmarks*' bdn-unary
  run_bdn '*StreamingBenchmarks*' bdn-streaming
  run_bdn '*AdmissionRpcBenchmarks*' bdn-admission
  run_bdn '*RuntimeHotPathBenchmarks*' bdn-runtime-hot-path
fi

SERVER_JIT_METHODS='*ProcessRequestLoop* *DispatchRpcAsync* *DispatchOneWayRpc* *InvokeServiceTrackedAsync*'
CLIENT_JIT_METHODS='*InvokeUnaryAsync* *InvokeUnaryCoreAsync* *InvokeUnaryWithOptionalRetryAsync* *InvokeUnaryWithRetryAsync* *InvokeUnaryRetryAttemptAsync* *SelectEndpoint* *SelectConnection*'

run_jit_probe() {
  local component="$1"
  local methods="$2"
  local mode="$3"
  local tiered_compilation="$4"
  local tiered_pgo="$5"
  local prefix="$JIT_ROOT/$component-$mode"
  DOTNET_TieredCompilation="$tiered_compilation" \
  DOTNET_TieredPGO="$tiered_pgo" \
  DOTNET_JitDisasm="$methods" \
  DOTNET_JitDisasmSummary=1 \
  DOTNET_JitStdOutFile="$prefix-disassembly.txt" \
  DOTNET_JitTimeLogFile="$prefix-jit-time.txt" \
  dotnet run -c Release --no-build --project "$PROJECT" -- \
    --jit-evidence "$component" 500 "$prefix.json" > "$prefix.stdout"
}

run_jit_probe server "$SERVER_JIT_METHODS" tiered 1 1
run_jit_probe client "$CLIENT_JIT_METHODS" tiered 1 1
run_jit_probe server "$SERVER_JIT_METHODS" fullopts 0 0
run_jit_probe client "$CLIENT_JIT_METHODS" fullopts 0 0

if command -v perf >/dev/null 2>&1; then
  PERF_EVENTS='cycles,instructions,branches,branch-misses,L1-icache-loads,L1-icache-load-misses,iTLB-loads,iTLB-load-misses'
  set +e
  perf stat -x, \
    -e "$PERF_EVENTS" \
    -- true > "$ENVIRONMENT_ROOT/perf-probe.txt" 2>&1
  PERF_STATUS=$?
  set -e
  if [[ "$PERF_STATUS" == "0" ]]; then
    set +e
    perf stat -x, -r 3 -e "$PERF_EVENTS" \
      -o "$ENVIRONMENT_ROOT/perf-server.csv" -- \
      dotnet run -c Release --no-build --project "$PROJECT" -- \
      --feature-evidence server AdmissionImmediate \
      "$WARMUP_OPERATIONS" "$MEASUREMENT_SECONDS" "$MAX_OPERATIONS" \
      "$ENVIRONMENT_ROOT/perf-server.json" \
      > "$ENVIRONMENT_ROOT/perf-server.stdout"
    PERF_SERVER_STATUS=$?
    perf stat -x, -r 3 -e "$PERF_EVENTS" \
      -o "$ENVIRONMENT_ROOT/perf-client.csv" -- \
      dotnet run -c Release --no-build --project "$PROJECT" -- \
      --feature-evidence client StaticFourEndpoints \
      "$WARMUP_OPERATIONS" "$MEASUREMENT_SECONDS" "$MAX_OPERATIONS" \
      "$ENVIRONMENT_ROOT/perf-client.json" \
      > "$ENVIRONMENT_ROOT/perf-client.stdout"
    PERF_CLIENT_STATUS=$?
    set -e
    if [[ "$PERF_SERVER_STATUS" == "0" && "$PERF_CLIENT_STATUS" == "0" ]] &&
       ! grep -Eq '<not supported>|<not counted>' \
         "$ENVIRONMENT_ROOT/perf-server.csv" "$ENVIRONMENT_ROOT/perf-client.csv"; then
      printf 'collected: server AdmissionImmediate and client StaticFourEndpoints, three repetitions each\n' \
        > "$ENVIRONMENT_ROOT/hardware-counters.status"
    else
      printf 'partially collected: server exit %s, client exit %s; inspect perf-*.csv\n' \
        "$PERF_SERVER_STATUS" "$PERF_CLIENT_STATUS" \
        > "$ENVIRONMENT_ROOT/hardware-counters.status"
    fi
  else
    printf 'not collected: perf stat exited %s; see perf-probe.txt\n' "$PERF_STATUS" \
      > "$ENVIRONMENT_ROOT/hardware-counters.status"
  fi
else
  printf 'not collected: perf is not installed\n' \
    > "$ENVIRONMENT_ROOT/hardware-counters.status"
fi

printf 'P2-00P baseline complete: %s\n' "$OUTPUT_ROOT"
