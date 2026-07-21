#!/usr/bin/env bash
set -euo pipefail

SCRIPT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ROOT="${SHARPLINK_EVIDENCE_ROOT:-$SCRIPT_ROOT}"
OUTPUT_ROOT="${SHARPLINK_EVIDENCE_OUTPUT:-$ROOT/artifacts/performance/v0.7.4}"
BASELINE_COMMIT="${SHARPLINK_EVIDENCE_BASELINE:-e02dd874}"
ROUNDS="${SHARPLINK_EVIDENCE_ROUNDS:-5}"
DISABLED_WARMUP="${SHARPLINK_EVIDENCE_DISABLED_WARMUP:-2}"
DISABLED_DURATION="${SHARPLINK_EVIDENCE_DISABLED_DURATION:-5}"
SCENARIO_WARMUP="${SHARPLINK_EVIDENCE_SCENARIO_WARMUP:-1}"
SCENARIO_DURATION="${SHARPLINK_EVIDENCE_SCENARIO_DURATION:-3}"
COMPRESSION_WARMUP="${SHARPLINK_EVIDENCE_COMPRESSION_WARMUP:-1}"
COMPRESSION_DURATION="${SHARPLINK_EVIDENCE_COMPRESSION_DURATION:-1}"
TEMP_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/sharplink-v074-evidence.XXXXXX")"
BASELINE_ROOT="$TEMP_ROOT/baseline"
BASELINE_REGISTERED=false

cleanup() {
  if [[ "$BASELINE_REGISTERED" == true ]]; then
    git -C "$ROOT" worktree remove --force "$BASELINE_ROOT" >/dev/null 2>&1 || true
  fi
  rmdir "$TEMP_ROOT" >/dev/null 2>&1 || true
}
trap cleanup EXIT

mkdir -p "$OUTPUT_ROOT/load" "$OUTPUT_ROOT/bin/v0.7.3" "$OUTPUT_ROOT/bin/v0.7.4"
git -C "$ROOT" worktree add --detach "$BASELINE_ROOT" "$BASELINE_COMMIT"
BASELINE_REGISTERED=true

dotnet build "$BASELINE_ROOT/test/SharpLink.LoadTest/SharpLink.LoadTest.csproj" -c Release -v minimal
dotnet build "$BASELINE_ROOT/test/SharpLink.LoadTest/SharpLink.LoadTest.csproj" -c Release -o "$OUTPUT_ROOT/bin/v0.7.3" -v minimal
dotnet build "$ROOT/test/SharpLink.LoadTest/SharpLink.LoadTest.csproj" -c Release -v minimal
dotnet build "$ROOT/test/SharpLink.LoadTest/SharpLink.LoadTest.csproj" -c Release -o "$OUTPUT_ROOT/bin/v0.7.4" -v minimal
dotnet build "$ROOT/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal

run_load() {
  local root="$1"
  shift
  dotnet run -c Release --no-build --project "$root/test/SharpLink.LoadTest" -- "$@"
}

run_disabled() {
  local root="$1"
  local label="$2"
  local round="$3"
  run_load "$root" \
    --mode local --transport tcp --operation add --concurrency 32 \
    --warmup "$DISABLED_WARMUP" --duration "$DISABLED_DURATION" --metrics-port 0 \
    --json-output "$OUTPUT_ROOT/load/disabled-$label-r$round.json"
}

for round in $(seq 1 "$ROUNDS"); do
  if (( round % 2 == 1 )); then
    run_disabled "$BASELINE_ROOT" v073 "$round"
    run_disabled "$ROOT" candidate "$round"
  else
    run_disabled "$ROOT" candidate "$round"
    run_disabled "$BASELINE_ROOT" v073 "$round"
  fi
done

for admission in immediate queue reject; do
  operation=add
  warmup="$DISABLED_WARMUP"
  duration="$DISABLED_DURATION"
  if [[ "$admission" != immediate ]]; then
    operation=delay
    warmup="$SCENARIO_WARMUP"
    duration="$SCENARIO_DURATION"
  fi
  for round in $(seq 1 "$ROUNDS"); do
    run_load "$ROOT" \
      --mode local --transport tcp --operation "$operation" --concurrency 32 \
      --admission "$admission" --warmup "$warmup" --duration "$duration" \
      --metrics-port 0 \
      --json-output "$OUTPUT_ROOT/load/admission-$admission-r$round.json"
  done
done

for algorithm in none brotli; do
  for pattern in compressible random; do
    for payload_size in 1024 4096 65536 1048576; do
      for round in $(seq 1 "$ROUNDS"); do
        run_load "$ROOT" \
          --mode local --transport tcp --operation echo --concurrency 8 \
          --payload-size "$payload_size" --compression "$algorithm" \
          --compression-level fastest \
          --max-send-queue-bytes 33554432 \
          --payload-pattern "$pattern" --warmup "$COMPRESSION_WARMUP" \
          --duration "$COMPRESSION_DURATION" --metrics-port 0 \
          --json-output "$OUTPUT_ROOT/load/compression-$algorithm-$pattern-$payload_size-r$round.json"
      done
    done
  done
done

# CompressionLevel is encode-only tuning for the built-in Brotli wire profile. Cover
# the larger payloads where its CPU/ratio tradeoff is measurable; the fastest
# rows above are the corresponding baseline for this configuration matrix.
for algorithm in brotli; do
  for compression_level in optimal smallest; do
    for pattern in compressible random; do
      for payload_size in 65536 1048576; do
        for round in $(seq 1 "$ROUNDS"); do
          run_load "$ROOT" \
            --mode local --transport tcp --operation echo --concurrency 8 \
            --payload-size "$payload_size" --compression "$algorithm" \
            --compression-level "$compression_level" --payload-pattern "$pattern" \
            --max-send-queue-bytes 33554432 \
            --warmup "$COMPRESSION_WARMUP" --duration "$COMPRESSION_DURATION" \
            --metrics-port 0 \
            --json-output "$OUTPUT_ROOT/load/compression-config-$algorithm-$compression_level-$pattern-$payload_size-r$round.json"
        done
      done
    done
  done
done

# A strict benefit policy demonstrates that configuration can bypass an
# otherwise negotiable algorithm without changing the established connection.
for payload_size in 4096 65536; do
  for round in $(seq 1 "$ROUNDS"); do
    run_load "$ROOT" \
      --mode local --transport tcp --operation echo --concurrency 8 \
      --payload-size "$payload_size" --compression brotli \
      --compression-level fastest --payload-pattern compressible \
      --max-send-queue-bytes 33554432 \
      --compression-min-payload 65536 --compression-min-savings-bytes 4096 \
      --compression-min-savings-ratio 0.10 \
      --warmup "$COMPRESSION_WARMUP" --duration "$COMPRESSION_DURATION" \
      --metrics-port 0 \
      --json-output "$OUTPUT_ROOT/load/compression-threshold-strict-brotli-compressible-$payload_size-r$round.json"
  done
done

dotnet run -c Release --no-build \
  --project "$ROOT/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -- \
  --compression-evidence --output "$OUTPUT_ROOT/compression-provider.json"

echo "SharpLink 0.7.4 performance evidence complete: $OUTPUT_ROOT"
