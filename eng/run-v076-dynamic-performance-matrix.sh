#!/usr/bin/env bash
set -euo pipefail

SCRIPT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ROOT="${SHARPLINK_V076_MATRIX_ROOT:-$SCRIPT_ROOT}"
TIER="${SHARPLINK_V076_MATRIX_TIER:-smoke}"
RUNTIMES="${SHARPLINK_V076_MATRIX_RUNTIMES:-jit}"
OUTPUT_ROOT="${SHARPLINK_V076_MATRIX_OUTPUT:-$ROOT/artifacts/performance/v0.7.6/dynamic}"
WARMUP="${SHARPLINK_V076_MATRIX_WARMUP:-1}"
DURATION="${SHARPLINK_V076_MATRIX_DURATION:-3}"

case "$TIER" in
  smoke)
    ENDPOINTS="1 2"
    CONCURRENCY="1 8"
    PAYLOADS="0 256"
    STRATEGIES="p2c leastpending"
    ;;
  full)
    ENDPOINTS="1 2 8 32"
    CONCURRENCY="1 8 32 128"
    PAYLOADS="0 32 256 4096 65536"
    STRATEGIES="p2c random roundrobin leastpending"
    ;;
  *)
    echo "Unsupported SHARPLINK_V076_MATRIX_TIER: $TIER" >&2
    exit 2
    ;;
esac

mkdir -p "$OUTPUT_ROOT"
cd "$ROOT"
dotnet build test/SharpLink.LoadTest/SharpLink.LoadTest.csproj -c Release -v minimal

RID=""
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) RID=osx-arm64 ;;
  Darwin-x86_64) RID=osx-x64 ;;
  Linux-x86_64) RID=linux-x64 ;;
  Linux-aarch64|Linux-arm64) RID=linux-arm64 ;;
  *) echo "Unsupported host for NativeAOT: $(uname -s)-$(uname -m)" >&2; exit 2 ;;
esac

run_case() {
  local runtime="$1"
  local endpoint_count="$2"
  local strategy="$3"
  local concurrency="$4"
  local payload="$5"
  local output="$6"
  local args=(
    --mode local --transport tcp --dynamic-endpoints "$endpoint_count"
    --load-balancing "$strategy" --operation echo --payload-size "$payload"
    --concurrency "$concurrency" --warmup "$WARMUP" --duration "$DURATION"
    --metrics-port 0 --json-output "$output")

  if [[ "$runtime" == "jit" ]]; then
    dotnet run -c Release --no-build --project test/SharpLink.LoadTest/SharpLink.LoadTest.csproj -- "${args[@]}"
  else
    "$OUTPUT_ROOT/aot/SharpLink.LoadTest" "${args[@]}"
  fi
}

IFS=',' read -r -a RUNTIME_LIST <<< "$RUNTIMES"
for runtime in "${RUNTIME_LIST[@]}"; do
  case "$runtime" in
    jit) ;;
    aot)
      dotnet publish test/SharpLink.LoadTest/SharpLink.LoadTest.csproj -c Release -r "$RID" \
        -p:PublishAot=true -o "$OUTPUT_ROOT/aot"
      ;;
    *) echo "Unsupported runtime: $runtime" >&2; exit 2 ;;
  esac

  for endpoint_count in $ENDPOINTS; do
    for strategy in $STRATEGIES; do
      for concurrency in $CONCURRENCY; do
        for payload in $PAYLOADS; do
          output="$OUTPUT_ROOT/${runtime}-e${endpoint_count}-${strategy}-c${concurrency}-p${payload}.json"
          run_case "$runtime" "$endpoint_count" "$strategy" "$concurrency" "$payload" "$output"
        done
      done
    done
  done
done

echo "0.7.6 dynamic endpoint performance matrix complete: $OUTPUT_ROOT"
