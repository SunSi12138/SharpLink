#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TIMESTAMP="$(date -u +%Y%m%d-%H%M%S)"
OUTPUT_ROOT="${1:-$ROOT/artifacts/runtime-phase00-baseline/$TIMESTAMP}"
BDN_JOB="${SHARPLINK_PHASE00_BDN_JOB:-Short}"
BDN_LAUNCH_COUNT="${SHARPLINK_PHASE00_BDN_LAUNCH_COUNT:-3}"
BDN_WARMUP_COUNT="${SHARPLINK_PHASE00_BDN_WARMUP_COUNT:-3}"
BDN_ITERATION_COUNT="${SHARPLINK_PHASE00_BDN_ITERATION_COUNT:-12}"
BDN_ITERATION_MILLISECONDS="${SHARPLINK_PHASE00_BDN_ITERATION_MILLISECONDS:-100}"
BENCHMARK_SHA="${SHARPLINK_BENCHMARK_SHA:-}"

if [[ -z "$BENCHMARK_SHA" ]] && [[ -d "$ROOT/.git" ]]; then
  BENCHMARK_SHA="$(git -C "$ROOT" rev-parse HEAD)"
fi
if [[ -z "$BENCHMARK_SHA" ]]; then
  echo "SHARPLINK_BENCHMARK_SHA is required when Git metadata is unavailable." >&2
  exit 2
fi
for count in "$BDN_LAUNCH_COUNT" "$BDN_WARMUP_COUNT" "$BDN_ITERATION_COUNT" "$BDN_ITERATION_MILLISECONDS"; do
  if [[ ! "$count" =~ ^[1-9][0-9]*$ ]]; then
    echo "Benchmark launch, warmup, iteration, and iteration-time values must be positive integers." >&2
    exit 2
  fi
done
if [[ -e "$OUTPUT_ROOT" ]]; then
  echo "Output path already exists; choose a fresh directory: $OUTPUT_ROOT" >&2
  exit 2
fi

mkdir -p "$OUTPUT_ROOT/environment" "$OUTPUT_ROOT/benchmark"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

{
  printf 'timestamp_utc=%s\n' "$(date -u --iso-8601=seconds)"
  printf 'benchmark_sha=%s\n' "$BENCHMARK_SHA"
  printf 'bdn_job=%s\n' "$BDN_JOB"
  printf 'bdn_launch_count=%s\n' "$BDN_LAUNCH_COUNT"
  printf 'bdn_warmup_count=%s\n' "$BDN_WARMUP_COUNT"
  printf 'bdn_iteration_count=%s\n' "$BDN_ITERATION_COUNT"
  printf 'bdn_iteration_milliseconds=%s\n' "$BDN_ITERATION_MILLISECONDS"
  uname -a
  lscpu
  free -h
  dotnet --info
} > "$OUTPUT_ROOT/environment/fingerprint.txt"

cd "$ROOT"
dotnet build test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release -v minimal \
  > "$OUTPUT_ROOT/build.log"
dotnet run -c Release --no-build --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -- \
  --filter '*RuntimePhase00Benchmarks*' \
  --job "$BDN_JOB" \
  --launchCount "$BDN_LAUNCH_COUNT" \
  --warmupCount "$BDN_WARMUP_COUNT" \
  --iterationCount "$BDN_ITERATION_COUNT" \
  --iterationTime "$BDN_ITERATION_MILLISECONDS" \
  --artifacts "$OUTPUT_ROOT/benchmark" \
  --exporters fulljson \
  --noOverwrite \
  > "$OUTPUT_ROOT/benchmark.log"

REPORT="$(find "$OUTPUT_ROOT/benchmark" -type f -name '*report-github.md' -print -quit)"
if [[ -z "$REPORT" ]]; then
  echo "BenchmarkDotNet did not produce a GitHub report." >&2
  exit 1
fi
for column in 'P50' 'P99' 'Op/s' 'Allocated'; do
  if ! grep -Fq "$column" "$REPORT"; then
    echo "Benchmark report is missing required column: $column" >&2
    exit 1
  fi
done

cp "$REPORT" "$OUTPUT_ROOT/runtime-phase00-summary.md"
printf 'Runtime Phase 00 baseline complete: %s\n' "$OUTPUT_ROOT"
