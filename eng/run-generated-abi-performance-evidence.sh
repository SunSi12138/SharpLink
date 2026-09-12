#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
OUTPUT="${SHARPLINK_GENERATED_ABI_OUTPUT:-$ROOT/artifacts/p3-generated-abi}"
BENCHMARK_SHA="${SHARPLINK_BENCHMARK_SHA:-$(git -C "$ROOT" rev-parse HEAD)}"
CPU_LIST="${SHARPLINK_BENCHMARK_CPU_LIST:-4-7}"
RUNS="${SHARPLINK_BENCHMARK_RUNS:-7}"
WARMUP="${SHARPLINK_BENCHMARK_WARMUP:-100}"
UNARY_WARMUP="${SHARPLINK_BENCHMARK_UNARY_WARMUP:-500}"
MEASUREMENT_SECONDS="${SHARPLINK_BENCHMARK_SECONDS:-3}"
MAX_OPERATIONS="${SHARPLINK_BENCHMARK_MAX_OPERATIONS:-200000}"

if [[ "$(uname -s)" != "Linux" ]] || ! command -v taskset >/dev/null 2>&1; then
  echo "Generated ABI performance evidence requires Linux taskset." >&2
  exit 2
fi
if ! [[ "$RUNS" =~ ^[1-9][0-9]*$ ]]; then
  echo "SHARPLINK_BENCHMARK_RUNS must be a positive integer." >&2
  exit 2
fi

mkdir -p "$OUTPUT/raw/streaming" "$OUTPUT/raw/unary" "$OUTPUT/logs"

dotnet build "$PROJECT" -c Release --no-restore -m:1 /nodeReuse:false -v minimal

forward=(
  Server1x16
  Server100x16
  Server100x4096
  Client100x16
  Client100x4096
  Duplex100x16
  Duplex100x4096
)
reverse=(
  Duplex100x4096
  Duplex100x16
  Client100x4096
  Client100x16
  Server100x4096
  Server100x16
  Server1x16
)

export SHARPLINK_BENCHMARK_SHA="$BENCHMARK_SHA"
for ((run = 1; run <= RUNS; run++)); do
  if ((run % 2 == 1)); then
    scenarios=("${forward[@]}")
  else
    scenarios=("${reverse[@]}")
  fi

  for scenario in "${scenarios[@]}"; do
    taskset -c "$CPU_LIST" dotnet run \
      -c Release --no-build --no-restore --project "$PROJECT" -- \
      --generated-abi-streaming-evidence \
      "$scenario" "$WARMUP" "$MEASUREMENT_SECONDS" "$MAX_OPERATIONS" \
      "$OUTPUT/raw/streaming/run-$run-$scenario.json" \
      >"$OUTPUT/logs/run-$run-$scenario.log"
  done

  taskset -c "$CPU_LIST" dotnet run \
    -c Release --no-build --no-restore --project "$PROJECT" -- \
    --feature-evidence server StaticDefault \
    "$UNARY_WARMUP" "$MEASUREMENT_SECONDS" "$MAX_OPERATIONS" \
    "$OUTPUT/raw/unary/run-$run-ServerStaticDefault.json" \
    >"$OUTPUT/logs/run-$run-ServerStaticDefault.log"
done

dotnet run -c Release --no-build --no-restore --project "$PROJECT" -- \
  --summarize-generated-abi-streaming-evidence \
  "$OUTPUT/raw/streaming" "$OUTPUT/streaming-summary.md" "$OUTPUT/streaming-results.jsonl"

echo "Generated ABI performance evidence completed at $OUTPUT."
