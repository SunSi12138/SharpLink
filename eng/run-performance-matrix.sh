#!/usr/bin/env bash
set -euo pipefail

SCRIPT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ROOT="${SHARPLINK_MATRIX_ROOT:-$SCRIPT_ROOT}"
TIER="${SHARPLINK_MATRIX_TIER:-smoke}"
RUNTIMES="${SHARPLINK_MATRIX_RUNTIMES:-jit}"
REPETITIONS="${SHARPLINK_MATRIX_REPETITIONS:-}"
OUTPUT_ROOT="${SHARPLINK_MATRIX_OUTPUT:-$ROOT/artifacts/perf/0.6.10-matrix}"

if [[ "$TIER" == "full" ]]; then
  DEFAULT_TRANSPORTS="tcp,uds,namedpipe,anonymous,sharedmemory"
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) DEFAULT_TRANSPORTS="tcp,namedpipe,anonymous,sharedmemory" ;;
  esac
  DEFAULT_PROFILES="balanced,lowlatency,throughput"
  DEFAULT_PAYLOADS="0,32,256,4096,65536,1048576"
  DEFAULT_CONCURRENCY="1,8,32,128"
  DEFAULT_WARMUP=5
  DEFAULT_DURATION=20
  DEFAULT_STREAM_OPERATION=all
  REPETITIONS="${REPETITIONS:-5}"
else
  DEFAULT_TRANSPORTS="tcp,sharedmemory"
  DEFAULT_PROFILES="balanced"
  DEFAULT_PAYLOADS="0,256,65536"
  DEFAULT_CONCURRENCY="1,32,128"
  DEFAULT_WARMUP=1
  DEFAULT_DURATION=3
  DEFAULT_STREAM_OPERATION=s2c
  REPETITIONS="${REPETITIONS:-1}"
fi

TRANSPORTS_CSV="${SHARPLINK_MATRIX_TRANSPORTS:-$DEFAULT_TRANSPORTS}"
PROFILES_CSV="${SHARPLINK_MATRIX_PROFILES:-$DEFAULT_PROFILES}"
PAYLOADS_CSV="${SHARPLINK_MATRIX_PAYLOADS:-$DEFAULT_PAYLOADS}"
CONCURRENCY="${SHARPLINK_MATRIX_CONCURRENCY:-$DEFAULT_CONCURRENCY}"
WARMUP="${SHARPLINK_MATRIX_WARMUP:-$DEFAULT_WARMUP}"
DURATION="${SHARPLINK_MATRIX_DURATION:-$DEFAULT_DURATION}"
STREAM_OPERATION="${SHARPLINK_MATRIX_STREAM_OPERATION:-$DEFAULT_STREAM_OPERATION}"
WORKLOADS=",${SHARPLINK_MATRIX_WORKLOADS:-unary,oneway,oneway-backpressure,async,streams},"
IFS=',' read -r -a TRANSPORTS <<< "$TRANSPORTS_CSV"
IFS=',' read -r -a PROFILES <<< "$PROFILES_CSV"
IFS=',' read -r -a PAYLOADS <<< "$PAYLOADS_CSV"

if [[ ! -d "$ROOT/test/SharpLink.LoadTest" || ! -d "$ROOT/test/SharpLink.StreamLoadTest" ]]; then
  echo "SharpLink matrix root is invalid: $ROOT" >&2
  exit 2
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
  MINGW*-x86_64|MSYS*-x86_64|CYGWIN*-x86_64) RID=win-x64 ;;
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
  local executable="$publish/$name"
  if [[ "$RID" == win-* ]]; then
    executable="$executable.exe"
  fi
  if [[ ! -x "$executable" ]]; then
    dotnet publish "$project" -c Release -r "$RID" /p:PublishAot=true -o "$publish" -v minimal
  fi
  "$executable" "$@"
}

IFS=',' read -r -a RUNTIME_LIST <<< "$RUNTIMES"
for runtime in "${RUNTIME_LIST[@]}"; do
  for repetition in $(seq 1 "$REPETITIONS"); do
    ordered_transports=("${TRANSPORTS[@]}")
    if (( repetition % 2 == 0 )); then
      ordered_transports=()
      for ((index=${#TRANSPORTS[@]}-1; index>=0; index--)); do
        ordered_transports+=("${TRANSPORTS[index]}")
      done
    fi
    for transport in "${ordered_transports[@]}"; do
      for profile in "${PROFILES[@]}"; do
        POOLS=("1 1" "1 4")
        if [[ "$transport" == "anonymous" ]]; then
          POOLS=("1 1")
        fi
        for pool in "${POOLS[@]}"; do
          read -r min_connections max_connections <<< "$pool"
          prefix="$OUTPUT_ROOT/$runtime-r$repetition-$transport-$profile-p$min_connections-$max_connections"

          if [[ "$WORKLOADS" == *,unary,* ]]; then
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
          fi

          # OneWay completes when the bounded local SendPump accepts the frame. A
          # sustained many-producer loop intentionally reaches that bound, so keep
          # the latency/throughput sample single-producer and record saturation as
          # a separate backpressure result instead of mixing the two semantics.
          if [[ "$WORKLOADS" == *,oneway,* ]]; then
            run_project "$runtime" test/SharpLink.LoadTest \
              --mode local --transport "$transport" --operation oneway \
              --payload-size 0 --concurrency 1 \
              --warmup "$WARMUP" --duration "$DURATION" --metrics-port 0 \
              --profile "$profile" --min-connections "$min_connections" \
              --max-connections "$max_connections" \
              --json-output "$prefix-oneway.json"
          fi

          if [[ "$runtime" == "jit" && "$WORKLOADS" == *,oneway-backpressure,* ]]; then
            run_project "$runtime" test/SharpLink.LoadTest \
              --mode local --transport "$transport" --operation oneway \
              --payload-size 0 --concurrency "$CONCURRENCY" \
              --warmup "$WARMUP" --duration "$DURATION" --metrics-port 0 \
              --profile "$profile" --min-connections "$min_connections" \
              --max-connections "$max_connections" \
              --json-output "$prefix-oneway-backpressure.json"
          fi

          if [[ "$WORKLOADS" == *,async,* ]]; then
            for operation in yield delay; do
              run_project "$runtime" test/SharpLink.LoadTest \
                --mode local --transport "$transport" --operation "$operation" \
                --payload-size 0 --concurrency "$CONCURRENCY" \
                --warmup "$WARMUP" --duration "$DURATION" --metrics-port 0 \
                --profile "$profile" --min-connections "$min_connections" \
                --max-connections "$max_connections" \
                --json-output "$prefix-$operation.json"
            done
          fi

          if [[ "$WORKLOADS" == *,streams,* ]]; then
            run_project "$runtime" test/SharpLink.StreamLoadTest \
              --mode local --transport "$transport" --operation "$STREAM_OPERATION" --stream-size 256 \
              --concurrency "$CONCURRENCY" --warmup "$WARMUP" --duration "$DURATION" \
              --profile "$profile" --min-connections "$min_connections" \
              --max-connections "$max_connections" \
              --json-output "$prefix-streams.json"
          fi
        done
      done
    done
  done
done

echo "SharpLink performance matrix complete: $OUTPUT_ROOT"
