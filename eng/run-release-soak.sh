#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DURATION_SECONDS="${SHARPLINK_SOAK_DURATION_SECONDS:-86400}"
CONCURRENCY="${SHARPLINK_SOAK_CONCURRENCY:-32}"
RESTART_SECONDS="${SHARPLINK_SOAK_RESTART_SECONDS:-60}"
OUTPUT="${SHARPLINK_SOAK_OUTPUT:-$ROOT/artifacts/chaos/release-24h.json}"

cd "$ROOT"
dotnet build test/SharpLink.ChaosTests/SharpLink.ChaosTests.csproj -c Release -v minimal
dotnet run -c Release --no-build \
  --project test/SharpLink.ChaosTests \
  -- \
  --duration-seconds "$DURATION_SECONDS" \
  --concurrency "$CONCURRENCY" \
  --restart-interval-seconds "$RESTART_SECONDS" \
  --json-output "$OUTPUT"

echo "SharpLink release soak passed: $OUTPUT"
