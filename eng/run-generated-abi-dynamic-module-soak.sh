#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj"
DURATION_SECONDS="${SHARPLINK_P3_DYNAMIC_SOAK_DURATION_SECONDS:-7200}"
OUTPUT="${SHARPLINK_P3_DYNAMIC_SOAK_OUTPUT:-$ROOT/artifacts/p3-generated-abi/dynamic-module-soak}"

if ! [[ "$DURATION_SECONDS" =~ ^[1-9][0-9]*$ ]]; then
  echo "SHARPLINK_P3_DYNAMIC_SOAK_DURATION_SECONDS must be a positive integer." >&2
  exit 2
fi
if ! command -v jq >/dev/null 2>&1; then
  echo "The generated ABI dynamic-module soak requires jq." >&2
  exit 2
fi

mkdir -p "$OUTPUT"
dotnet build "$PROJECT" -c Release -m:1 -p:UseSharedCompilation=false -nodeReuse:false -v minimal
dotnet run -c Release --no-build --no-restore --project "$PROJECT" -- \
  --list-tests json >"$OUTPUT/tests.json"

stream_uid="$(jq -r '.tests[] | select(.displayName == "ServerStreamConsumerExitShouldReleaseDynamicModuleLeasesAndAllCounters") | .uid' "$OUTPUT/tests.json")"
replacement_uid="$(jq -r '.tests[] | select(.displayName == "OneHundredDynamicModuleReplacementsShouldPublishNewRouteWhileOldUnaryDrainsWithoutLeaks") | .uid' "$OUTPUT/tests.json")"
if [[ -z "$stream_uid" || "$stream_uid" == "null" ||
      -z "$replacement_uid" || "$replacement_uid" == "null" ]]; then
  echo "Required generated ABI dynamic-module tests were not discovered." >&2
  exit 2
fi

started_epoch="$(date +%s)"
deadline_epoch=$((started_epoch + DURATION_SECONDS))
rounds=0
: >"$OUTPUT/test.log"
while (( $(date +%s) < deadline_epoch )); do
  dotnet run -c Release --no-build --no-restore --project "$PROJECT" -- \
    --maximum-parallel-tests 1 --timeout 120s --filter-uid "$replacement_uid" \
    >>"$OUTPUT/test.log" 2>&1
  dotnet run -c Release --no-build --no-restore --project "$PROJECT" -- \
    --maximum-parallel-tests 1 --timeout 120s --filter-uid "$stream_uid" \
    >>"$OUTPUT/test.log" 2>&1
  rounds=$((rounds + 1))
done

ended_epoch="$(date +%s)"
elapsed_seconds=$((ended_epoch - started_epoch))
if (( rounds == 0 || elapsed_seconds < DURATION_SECONDS )); then
  echo "Dynamic-module soak ended without the requested coverage." >&2
  exit 3
fi

printf 'commit=%s\nduration_seconds=%s\nrounds=%s\nreplacements=%s\nstream_consumer_exits=%s\n' \
  "$(git -C "$ROOT" rev-parse HEAD)" \
  "$elapsed_seconds" \
  "$rounds" \
  "$((rounds * 100))" \
  "$rounds" \
  >"$OUTPUT/summary.txt"
cat "$OUTPUT/summary.txt"
