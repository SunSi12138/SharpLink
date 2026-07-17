#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TIER="${SHARPLINK_MATRIX_TIER:-smoke}"
RUNTIMES="${SHARPLINK_MATRIX_RUNTIMES:-jit}"
REPETITIONS="${SHARPLINK_MATRIX_REPETITIONS:-1}"
OUTPUT_ROOT="${SHARPLINK_MATRIX_OUTPUT:-$ROOT/artifacts/perf/0.6.9-matrix}"

if [[ "$TIER" == "full" ]]; then
  TRANSPORTS=(tcp uds namedpipe anonymous)
  PROFILES=(balanced lowlatency throughput)
  PAYLOADS=(0 32 256 4096 65536)
  CONCURRENCY="1,8,32,128"
  WARMUP=5
  DURATION=20
  STREAM_OPERATION=all
else
  TRANSPORTS=(tcp)
  PROFILES=(balanced)
  PAYLOADS=(0 256 65536)
  CONCURRENCY="1,32,128"
  WARMUP=1
  DURATION=3
  STREAM_OPERATION=s2c
fi

mkdir -p "$OUTPUT_ROOT"
cd "$ROOT"
dotnet build test/SharpLink.LoadTest/SharpLink.LoadTest.csproj -c Release -v minimal
dotnet build test/SharpLink.StreamLoadTest/SharpLink.StreamLoadTest.csproj -c Release -v minimal

RID=""
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) RID=linux-x64 ;;
  Linux-aarch64|Linux-arm64) RID=linux-arm64 ;;
  Darwin-x86_64) RID=osx-x64 ;;
  Darwin-arm64) RID=osx-arm64 ;;
esac

run_project() {
  local runtime="$1"
  local project="$2"
  shift 2
  if [[ "$runtime" == "jit" ]]; then
    dotnet run -c Release --no-build --project "$project" -- "$@"
    return
  fi
  if [[ -z "$RID" ]]; then
    echo "NativeAOT is unsupported for this host architecture." >&2
    exit 2
  fi
  local name
  name="$(basename "$project")"
  local publish="$OUTPUT_ROOT/aot/$name"
  if [[ ! -x "$publish/$name" ]]; then
    dotnet publish "$project" -c Release -r "$RID" /p:PublishAot=true -o "$publish" -v minimal
  fi
  "$publish/$name" "$@"
}

IFS=',' read -r -a RUNTIME_LIST <<< "$RUNTIMES"
for runtime in "${RUNTIME_LIST[@]}"; do
  for repetition in $(seq 1 "$REPETITIONS"); do
    for transport in "${TRANSPORTS[@]}"; do
      for profile in "${PROFILES[@]}"; do
        POOLS=("1 1" "1 4")
        if [[ "$transport" == "anonymous" ]]; then
          POOLS=("1 1")
        fi
        for pool in "${POOLS[@]}"; do
          read -r min_connections max_connections <<< "$pool"
          prefix="$OUTPUT_ROOT/$runtime-r$repetition-$transport-$profile-p$min_connections-$max_connections"

          for payload in "${PAYLOADS[@]}"; do
            operation=echo
            if [[ "$payload" == "0" ]]; then
              operation=empty
            fi
            run_project "$runtime" test/SharpLink.LoadTest \
              --mode local --transport "$transport" --operation "$operation" \
              --payload-size "$payload" --concurrency "$CONCURRENCY" \
              --warmup "$WARMUP" --duration "$DURATION" --metrics-port 0 \
              --profile "$profile" --min-connections "$min_connections" \
              --max-connections "$max_connections" \
              --json-output "$prefix-unary-$payload.json"
          done

          # OneWay completes when the bounded local SendPump accepts the frame. A
          # sustained many-producer loop intentionally reaches that bound, so keep
          # the latency/throughput sample single-producer and record saturation as
          # a separate backpressure result instead of mixing the two semantics.
          run_project "$runtime" test/SharpLink.LoadTest \
            --mode local --transport "$transport" --operation oneway \
            --payload-size 0 --concurrency 1 \
            --warmup "$WARMUP" --duration "$DURATION" --metrics-port 0 \
            --profile "$profile" --min-connections "$min_connections" \
            --max-connections "$max_connections" \
            --json-output "$prefix-oneway.json"

          if [[ "$runtime" == "jit" ]]; then
            run_project "$runtime" test/SharpLink.LoadTest \
              --mode local --transport "$transport" --operation oneway \
              --payload-size 0 --concurrency "$CONCURRENCY" \
              --warmup "$WARMUP" --duration "$DURATION" --metrics-port 0 \
              --profile "$profile" --min-connections "$min_connections" \
              --max-connections "$max_connections" \
              --json-output "$prefix-oneway-backpressure.json"
          fi

          for operation in yield delay; do
            run_project "$runtime" test/SharpLink.LoadTest \
              --mode local --transport "$transport" --operation "$operation" \
              --payload-size 0 --concurrency "$CONCURRENCY" \
              --warmup "$WARMUP" --duration "$DURATION" --metrics-port 0 \
              --profile "$profile" --min-connections "$min_connections" \
              --max-connections "$max_connections" \
              --json-output "$prefix-$operation.json"
          done

          run_project "$runtime" test/SharpLink.StreamLoadTest \
            --mode local --transport "$transport" --operation "$STREAM_OPERATION" --stream-size 256 \
            --concurrency "$CONCURRENCY" --warmup "$WARMUP" --duration "$DURATION" \
            --profile "$profile" --min-connections "$min_connections" \
            --max-connections "$max_connections" \
            --json-output "$prefix-streams.json"
        done
      done
    done
  done
done

echo "SharpLink performance matrix complete: $OUTPUT_ROOT"
