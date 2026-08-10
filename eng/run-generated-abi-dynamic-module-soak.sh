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

replacement_uid="$(jq -r '.tests[] | select(.displayName == "OneHundredDynamicModuleReplacementsShouldPublishNewRouteWhileOldUnaryDrainsWithoutLeaks") | .uid' "$OUTPUT/tests.json")"
rejection_uid="$(jq -r '.tests[] | select(.displayName == "RejectedApi4DynamicRegistrationShouldReleaseItsCollectibleContext") | .uid' "$OUTPUT/tests.json")"
framework_unload_uid="$(jq -r '.tests[] | select(.displayName == "CollectibleContextShouldUnloadAfterFrameworkReferencesAreReleased") | .uid' "$OUTPUT/tests.json")"
mapfile -t stream_uids < <(jq -r '
  .tests[] |
  select(.displayName | startswith("Api4DynamicStreamExitShouldReleaseItsCollectibleContext(")) |
  .uid' "$OUTPUT/tests.json")
if [[ -z "$replacement_uid" || "$replacement_uid" == "null" ||
      -z "$rejection_uid" || "$rejection_uid" == "null" ||
      -z "$framework_unload_uid" || "$framework_unload_uid" == "null" ||
      ${#stream_uids[@]} -ne 5 ]]; then
  echo "Required generated ABI dynamic-module tests were not discovered." >&2
  exit 2
fi

run_test() {
  local uid="$1"
  dotnet run -c Release --no-build --no-restore --project "$PROJECT" -- \
    --maximum-parallel-tests 1 --timeout 120s --filter-uid "$uid" \
    >>"$OUTPUT/test.log" 2>&1
}

started_epoch="$(date +%s)"
deadline_epoch=$((started_epoch + DURATION_SECONDS))
rounds=0
: >"$OUTPUT/test.log"
while (( $(date +%s) < deadline_epoch )); do
  run_test "$replacement_uid"
  for stream_uid in "${stream_uids[@]}"; do
    run_test "$stream_uid"
  done
  run_test "$rejection_uid"
  run_test "$framework_unload_uid"
  rounds=$((rounds + 1))
done

ended_epoch="$(date +%s)"
elapsed_seconds=$((ended_epoch - started_epoch))
if (( rounds == 0 || elapsed_seconds < DURATION_SECONDS )); then
  echo "Dynamic-module soak ended without the requested coverage." >&2
  exit 3
fi

printf 'commit=%s\nduration_seconds=%s\nrounds=%s\ntest_processes=%s\nreplacements=%s\napi4_stream_exits=%s\napi4_stream_exit_modes=%s\nregistration_rejections=%s\nframework_reference_unloads=%s\n' \
  "$(git -C "$ROOT" rev-parse HEAD)" \
  "$elapsed_seconds" \
  "$rounds" \
  "$((rounds * (3 + ${#stream_uids[@]})))" \
  "$((rounds * 100))" \
  "$((rounds * ${#stream_uids[@]}))" \
  'normal,cancellation-before-first,cancellation-mid-stream,consumer-break,service-exception' \
  "$rounds" \
  "$rounds" \
  >"$OUTPUT/summary.txt"
cat "$OUTPUT/summary.txt"
