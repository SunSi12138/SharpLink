#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TIMESTAMP="$(date -u +%Y%m%d-%H%M%S)"
OUTPUT_ROOT="${1:-$ROOT/artifacts/latency-recorder-baseline/$TIMESTAMP}"
RUNS="${SHARPLINK_RECORDER_RUNS:-5}"
WARMUP_SECONDS="${SHARPLINK_RECORDER_WARMUP_SECONDS:-5}"
MEASUREMENT_SECONDS="${SHARPLINK_RECORDER_MEASUREMENT_SECONDS:-10}"
MAXIMUM_SAMPLES="${SHARPLINK_RECORDER_MAXIMUM_SAMPLES:-25000000}"
MICRO_RECORDS="${SHARPLINK_RECORDER_MICRO_RECORDS:-1000000}"
SOURCE_COMMIT="${SHARPLINK_COMMIT:-$(git -C "$ROOT" rev-parse HEAD)}"

if (( RUNS < 5 )); then
  echo "SHARPLINK_RECORDER_RUNS must be at least 5 for a formal gate." >&2
  exit 2
fi

if [[ -e "$OUTPUT_ROOT" ]]; then
  echo "Output path already exists; choose a fresh directory: $OUTPUT_ROOT" >&2
  exit 2
fi
mkdir -p "$OUTPUT_ROOT/environment" "$OUTPUT_ROOT/micro" "$OUTPUT_ROOT/macro" "$OUTPUT_ROOT/matrix" "$OUTPUT_ROOT/stream"
mkdir -p "$OUTPUT_ROOT/feature"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export SHARPLINK_COMMIT="$SOURCE_COMMIT"
export SHARPLINK_BENCHMARK_SHA="$SOURCE_COMMIT"

{
  printf 'timestamp_utc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf 'source_commit=%s\n' "$SOURCE_COMMIT"
  printf 'runs=%s\n' "$RUNS"
  printf 'warmup_seconds=%s\n' "$WARMUP_SECONDS"
  printf 'measurement_seconds=%s\n' "$MEASUREMENT_SECONDS"
  printf 'maximum_samples=%s\n' "$MAXIMUM_SAMPLES"
  uname -a
  dotnet --info
  if command -v lscpu >/dev/null 2>&1; then lscpu; fi
  if [[ -r /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor ]]; then
    printf 'scaling_governor='
    cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor
  fi
} > "$OUTPUT_ROOT/environment/fingerprint.txt"

cd "$ROOT"
dotnet build Sharplink.slnx -c Release -v minimal > "$OUTPUT_ROOT/build.log"
dotnet test --project test/SharpLink.LoadTest.Tests/SharpLink.LoadTest.Tests.csproj \
  -c Release --no-build > "$OUTPUT_ROOT/load-test-tests.log"
dotnet test --project test/SharpLink.StreamLoadTest.Tests/SharpLink.StreamLoadTest.Tests.csproj \
  -c Release --no-build > "$OUTPUT_ROOT/stream-load-test-tests.log"
dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj \
  -c Release --no-build > "$OUTPUT_ROOT/unit-tests.log"

dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
  -c Release --no-build -- \
  --latency-recorder-evidence "$MICRO_RECORDS" "$RUNS" \
  "$OUTPUT_ROOT/micro/latency-recorder.json" \
  > "$OUTPUT_ROOT/micro/latency-recorder.stdout"

run_load() {
  local output="$1"
  shift
  dotnet run --project test/SharpLink.LoadTest/SharpLink.LoadTest.csproj \
    -c Release --no-build -- \
    --mode local --duration "$MEASUREMENT_SECONDS" --warmup "$WARMUP_SECONDS" \
    --maximum-recorded-operations "$MAXIMUM_SAMPLES" --metrics-port 0 \
    --json-output "$output" "$@" > "$output.stdout"
}

for concurrency in 128 512; do
  for repetition in $(seq 1 "$RUNS"); do
    if (( repetition % 2 == 1 )); then modes=(off formal); else modes=(formal off); fi
    for mode in "${modes[@]}"; do
      run_load "$OUTPUT_ROOT/macro/c${concurrency}-r${repetition}-${mode}.json" \
        --transport tcp --profile balanced --operation add \
        --concurrency "$concurrency" --recording "$mode"
    done
  done
done

for concurrency in 128 512; do
  for repetition in $(seq 1 "$RUNS"); do
    if (( repetition % 2 == 1 )); then modes=(off formal); else modes=(formal off); fi
    for mode in "${modes[@]}"; do
      run_load "$OUTPUT_ROOT/macro/c${concurrency}-r${repetition}-tail-${mode}.json" \
        --transport tcp --profile balanced --operation add \
        --concurrency "$concurrency" --recording "$mode" --tail-observer
    done
  done
done

for concurrency in 128 512; do
  run_load "$OUTPUT_ROOT/macro/c${concurrency}-validation-dual.json" \
    --transport tcp --profile balanced --operation add \
    --concurrency "$concurrency" --recording validation-dual
done

dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
  -c Release --no-build -- \
  --analyze-latency-recorder-baseline \
  "$OUTPUT_ROOT/micro/latency-recorder.json" "$OUTPUT_ROOT/macro" "$RUNS" \
  "$OUTPUT_ROOT/macro/gate-analysis.json" \
  > "$OUTPUT_ROOT/macro/gate-analysis.stdout"

run_load "$OUTPUT_ROOT/matrix/metrics-enabled-add.json" \
  --transport tcp --profile balanced --operation add --concurrency 128 \
  --recording formal --metrics-port 9464
run_load "$OUTPUT_ROOT/matrix/static-four-endpoints-add.json" \
  --transport tcp --profile balanced --operation add --concurrency 128 \
  --recording formal --static-endpoints 4
run_load "$OUTPUT_ROOT/matrix/dynamic-four-endpoints-add.json" \
  --transport tcp --profile balanced --operation add --concurrency 128 \
  --recording formal --dynamic-endpoints 4

for entry in \
  "server StaticDefault" \
  "server MetricsClientAndServer" \
  "server ServerTraceOnePercent" \
  "client FixedDefault" \
  "client MetricsClientAndServer" \
  "client ClientTraceOnePercent" \
  "client StaticFourEndpoints" \
  "client DynamicFourEndpoints"; do
  read -r component scenario <<< "$entry"
  dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
    -c Release --no-build -- \
    --feature-evidence "$component" "$scenario" 1000 "$MEASUREMENT_SECONDS" "$MAXIMUM_SAMPLES" \
    "$OUTPUT_ROOT/feature/${component}-${scenario}.json" \
    > "$OUTPUT_ROOT/feature/${component}-${scenario}.stdout"
done

for transport in tcp sharedmemory; do
  for profile in lowlatency balanced throughput; do
    for operation in empty add echo; do
      run_load "$OUTPUT_ROOT/matrix/${transport}-${profile}-${operation}.json" \
        --transport "$transport" --profile "$profile" --operation "$operation" \
        --concurrency 1,8,32,128,512 --recording formal
      if [[ "$operation" == "echo" ]]; then
        run_load "$OUTPUT_ROOT/matrix/${transport}-${profile}-echo-medium.json" \
          --transport "$transport" --profile "$profile" --operation echo \
          --payload-size 65536 --concurrency 1,8,32,128 --recording formal
      fi
    done
  done
done

run_stream() {
  local output="$1"
  shift
  dotnet run --project test/SharpLink.StreamLoadTest/SharpLink.StreamLoadTest.csproj \
    -c Release --no-build -- \
    --mode local --duration "$MEASUREMENT_SECONDS" --warmup "$WARMUP_SECONDS" \
    --maximum-recorded-operations "$MAXIMUM_SAMPLES" \
    --json-output "$output" "$@" > "$output.stdout"
}

for transport in tcp sharedmemory; do
  for operation in unary c2s s2c duplex duplex-equivalent; do
    run_stream "$OUTPUT_ROOT/stream/${transport}-${operation}.json" \
      --transport "$transport" --operation "$operation" \
      --concurrency 1,8,32,128 --recording formal
  done
done

printf 'Latency recorder baseline complete: %s\n' "$OUTPUT_ROOT"
