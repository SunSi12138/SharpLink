#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TIMESTAMP="$(date -u +%Y%m%d-%H%M%S)"
OUTPUT_ROOT="${1:-${SHARPLINK_CALL_SHAPE_OUTPUT:-$ROOT/artifacts/client-call-shape-defolding/$TIMESTAMP}}"
RUNS="${SHARPLINK_CALL_SHAPE_RUNS:-3}"
WARMUP_OPERATIONS="${SHARPLINK_CALL_SHAPE_WARMUP_OPERATIONS:-2000}"
MEASUREMENT_SECONDS="${SHARPLINK_CALL_SHAPE_MEASUREMENT_SECONDS:-1}"
MAX_OPERATIONS="${SHARPLINK_CALL_SHAPE_MAX_OPERATIONS:-250000}"
PROBE_ITERATIONS="${SHARPLINK_CALL_SHAPE_PROBE_ITERATIONS:-30000}"
AOT_PROBE_ITERATIONS="${SHARPLINK_CALL_SHAPE_AOT_PROBE_ITERATIONS:-10000}"
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
   [[ ! "$PROBE_ITERATIONS" =~ ^[1-9][0-9]*$ ]] ||
   [[ ! "$AOT_PROBE_ITERATIONS" =~ ^[1-9][0-9]*$ ]]; then
  echo "Run counts and operation counts must be non-negative/positive integers as appropriate." >&2
  exit 2
fi
if [[ -e "$OUTPUT_ROOT" ]]; then
  echo "Output path already exists; choose a fresh directory: $OUTPUT_ROOT" >&2
  exit 2
fi

mkdir -p "$OUTPUT_ROOT/environment" "$OUTPUT_ROOT/probe" "$OUTPUT_ROOT/full-rpc"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export SHARPLINK_BENCHMARK_SHA="$BENCHMARK_SHA"

{
  printf 'timestamp_utc=%s\n' "$(date -u --iso-8601=seconds)"
  printf 'benchmark_sha=%s\n' "$BENCHMARK_SHA"
  printf 'runs=%s\n' "$RUNS"
  printf 'warmup_operations=%s\n' "$WARMUP_OPERATIONS"
  printf 'measurement_seconds=%s\n' "$MEASUREMENT_SECONDS"
  printf 'max_operations=%s\n' "$MAX_OPERATIONS"
  printf 'probe_iterations=%s\n' "$PROBE_ITERATIONS"
  printf 'aot_probe_iterations=%s\n' "$AOT_PROBE_ITERATIONS"
  uname -a
  dotnet --info
} > "$OUTPUT_ROOT/environment/fingerprint.txt"
if command -v lscpu >/dev/null 2>&1; then
  lscpu > "$OUTPUT_ROOT/environment/cpu.txt"
fi

cd "$ROOT"
python3 eng/client-call-shape-defolding.py self-test   > "$OUTPUT_ROOT/lattice-self-test.json"
python3 eng/client-call-shape-defolding.py lattice   "$OUTPUT_ROOT/lattice.json"

BENCHMARK_PROJECT="test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
dotnet build "$BENCHMARK_PROJECT" -c Release -v minimal   > "$OUTPUT_ROOT/benchmark-build.log"

INSPECTOR="$OUTPUT_ROOT/inspector"
python3 eng/client-call-shape-defolding.py generate-inspector "$INSPECTOR"
dotnet build "$INSPECTOR/Inspector.csproj" -c Release -v minimal -o "$INSPECTOR/bin"   > "$INSPECTOR/build.log"

for variant in A B C D; do
  directory="$OUTPUT_ROOT/probe/$variant"
  python3 eng/client-call-shape-defolding.py generate "$variant" "$directory"

  dotnet build "$directory/ShapeProbe.csproj" -c Release -v minimal -o "$directory/jit"     > "$directory/jit-build.log"

  dotnet "$INSPECTOR/bin/Inspector.dll"     "$directory/jit/ShapeProbe.dll"     "$directory/representatives.txt"     "$directory/inspector.json"

  JIT_METHODS=""
  while IFS= read -r method; do
    [[ -z "$method" ]] && continue
    JIT_METHODS+=" ShapeProbe.Program+<$method>d__*:MoveNext"
  done < "$directory/representatives.txt"

  DOTNET_TieredCompilation=1   DOTNET_TieredPGO=0   DOTNET_TC_QuickJitForLoops=1   DOTNET_JitDisasm="$JIT_METHODS"   DOTNET_JitDisasmSummary=1   DOTNET_JitStdOutFile="$directory/jit-disasm.txt"     dotnet "$directory/jit/ShapeProbe.dll"       "$PROBE_ITERATIONS" "$directory/jit-pgo-off.json"       > "$directory/jit-pgo-off.stdout"

  if ! grep -q "Total bytes of code" "$directory/jit-disasm.txt"; then
    echo "JIT disassembly did not report native code sizes for variant $variant." >&2
    exit 1
  fi

  DOTNET_TieredCompilation=1   DOTNET_TieredPGO=1   DOTNET_TC_QuickJitForLoops=1     dotnet "$directory/jit/ShapeProbe.dll"       "$PROBE_ITERATIONS" "$directory/jit-pgo-on.json"       > "$directory/jit-pgo-on.stdout"

  /usr/bin/time -f '%e' -o "$directory/aot-build-seconds.txt"     dotnet publish "$directory/ShapeProbe.csproj"       -c Release -r linux-x64 -p:PublishAot=true       -o "$directory/aot" -v minimal       > "$directory/aot-build.log"

  "$directory/aot/ShapeProbe"     "$AOT_PROBE_ITERATIONS" "$directory/aot-run.json"     > "$directory/aot-run.stdout"
done

SCENARIOS=(
  UnaryIdempotentValuePayload
  UnaryNonIdempotentValuePayload
  UnaryTimedIdempotentValuePayload
  UnaryCancellableValuePayload
  UnaryNoPayloadValue
  UnaryNoPayloadNoResponse
  UnaryRequiredReference
  UnaryNullableReference
  UnaryBytes4096
  OneWayPayload
  OneWayTimedPayload
  OneWayCancellablePayload
  OneWayOneClientStream
  OneWayTwoClientStreamsTimed
  ClientStreamingOneStream
  ClientStreamingTwoStreams
  ClientStreamingCancellable
  ClientStreamingPayload4096
  ServerStreamingValue
  ServerStreamingCancellable
  ServerStreamingPayload4096
  DuplexRequiredReference
  DuplexCancellable
  DuplexPayload4096
)

run_full_rpc() {
  local pgo="$1"
  local mode="$2"
  local scenario="$3"
  local repetition="$4"
  local directory="$OUTPUT_ROOT/full-rpc/$mode"
  mkdir -p "$directory"
  local output="$directory/$scenario-r$(printf '%02d' "$repetition").json"

  DOTNET_TieredCompilation=1   DOTNET_TieredPGO="$pgo"   DOTNET_TC_QuickJitForLoops=1     dotnet run -c Release --no-build       --project "$BENCHMARK_PROJECT" --       --client-call-shape-evidence       "$scenario" "$WARMUP_OPERATIONS" "$MEASUREMENT_SECONDS"       "$MAX_OPERATIONS" "$output"       > "$output.stdout"
}

for repetition in $(seq 1 "$RUNS"); do
  if (( repetition % 2 == 1 )); then
    for scenario in "${SCENARIOS[@]}"; do
      run_full_rpc 0 pgo-off "$scenario" "$repetition"
      run_full_rpc 1 pgo-on "$scenario" "$repetition"
    done
  else
    for ((index=${#SCENARIOS[@]} - 1; index >= 0; index--)); do
      scenario="${SCENARIOS[index]}"
      run_full_rpc 1 pgo-on "$scenario" "$repetition"
      run_full_rpc 0 pgo-off "$scenario" "$repetition"
    done
  fi
done

python3 eng/client-call-shape-defolding.py summarize   "$OUTPUT_ROOT"   "$OUTPUT_ROOT/summary.md"   "$OUTPUT_ROOT/summary.json"

cat "$OUTPUT_ROOT/summary.md"
