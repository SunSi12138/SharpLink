#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DURATION="${SHARPLINK_SOAK_DURATION:-${SHARPLINK_SOAK_DURATION_SECONDS:+${SHARPLINK_SOAK_DURATION_SECONDS}s}}"
DURATION="${DURATION:-24h}"
CONCURRENCY="${SHARPLINK_SOAK_CONCURRENCY:-32}"
RESTART_SECONDS="${SHARPLINK_SOAK_RESTART_SECONDS:-60}"
CHECKPOINT_INTERVAL="${SHARPLINK_SOAK_CHECKPOINT_INTERVAL:-30m}"
DUMP_ON_FAILURE="${SHARPLINK_SOAK_DUMP_ON_FAILURE:-true}"
STOP_ON_UNEXPECTED="${SHARPLINK_SOAK_STOP_ON_UNEXPECTED:-true}"
OUTPUT="${SHARPLINK_SOAK_OUTPUT:-$ROOT/artifacts/chaos/release-24h.json}"

cd "$ROOT"
dotnet build test/SharpLink.ChaosTests/SharpLink.ChaosTests.csproj -c Release -v minimal
dotnet run -c Release --no-build \
  --project test/SharpLink.ChaosTests \
  -- \
  --duration "$DURATION" \
  --concurrency "$CONCURRENCY" \
  --restart-interval-seconds "$RESTART_SECONDS" \
  --checkpoint-interval "$CHECKPOINT_INTERVAL" \
  --dump-on-failure "$DUMP_ON_FAILURE" \
  --stop-on-unexpected "$STOP_ON_UNEXPECTED" \
  --json-output "$OUTPUT"

echo "SharpLink release soak passed: $OUTPUT"
