#!/usr/bin/env bash
set -euo pipefail

configuration="${SHARPLINK_ALLOCATION_CONFIGURATION:-Release}"
if [[ "$configuration" != "Release" ]]; then
  echo "::error::Allocation regression gate must run in Release configuration."
  exit 2
fi

budget_path="${SHARPLINK_ALLOCATION_BUDGETS:-eng/perf/allocation-budgets.json}"
output_path="${SHARPLINK_ALLOCATION_OUTPUT:-artifacts/perf/allocation-gate.json}"
mkdir -p "$(dirname "$output_path")"

dotnet run \
  --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
  -c Release \
  --no-build \
  -- \
  --allocation-gate \
  --budgets "$budget_path" \
  --output "$output_path" \
  "$@"
