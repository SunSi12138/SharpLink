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

set +e
dotnet run \
  --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
  -c Release \
  --no-build \
  -- \
  --allocation-gate \
  --budgets "$budget_path" \
  --output "$output_path" \
  "$@"
status=$?
set -e

if [[ $status -ne 0 && -f "$output_path" ]]; then
  python3 - "$output_path" <<'PY'
import json
import math
import sys

path = sys.argv[1]
try:
    with open(path, encoding="utf-8") as stream:
        report = json.load(stream)
except Exception as exc:
    print(f"[AllocationGate] unable to read failure report {path}: {exc}", file=sys.stderr)
    sys.exit(0)

for item in report.get("cases", []):
    if item.get("passed", False):
        continue

    name = item.get("name", "<unknown>")
    observed = float(item.get("medianBytesPerOperation", 0.0))
    allowed = float(item.get("maxBytesPerOperation", 0.0))
    spread = float(item.get("spreadBytesPerOperation", 0.0))
    spread_allowed = float(item.get("maxSpreadBytesPerOperation", 0.0))
    delta = observed - allowed
    percent = delta / allowed * 100.0 if allowed > 0 else math.inf
    percent_text = f"{percent:+.1f}%" if math.isfinite(percent) else "n/a"
    reason = item.get("failure") or "allocation budget check failed"

    print(f"[AllocationGate] FAIL {name}", file=sys.stderr)
    print(f"  observed median: {observed:.3f} B/op", file=sys.stderr)
    print(f"  allowed median:  {allowed:.3f} B/op", file=sys.stderr)
    print(f"  regression:      {delta:+.3f} B/op ({percent_text})", file=sys.stderr)
    print(f"  observed spread: {spread:.3f} B/op", file=sys.stderr)
    print(f"  allowed spread:  {spread_allowed:.3f} B/op", file=sys.stderr)
    print(f"  reason:          {reason}", file=sys.stderr)
PY
fi

exit "$status"
