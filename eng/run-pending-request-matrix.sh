#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

TIER="${1:-${SHARPLINK_PENDING_MATRIX_TIER:-ci}}"
OUTPUT_DIR="${2:-${SHARPLINK_PENDING_MATRIX_OUTPUT:-artifacts/perf/pending-request-matrix}}"
REPORT="$OUTPUT_DIR/report.json"
mkdir -p "$OUTPUT_DIR"

case "$TIER" in
  ci|p0|p1) ;;
  *) echo "pending request matrix tier must be ci, p0, or p1" >&2; exit 2 ;;
esac

dotnet build test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release -v minimal

GITHUB_SHA="$(git rev-parse HEAD)" dotnet run \
  --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
  -c Release --no-build -- \
  --pending-request-matrix-evidence \
  --tier "$TIER" \
  --output "$REPORT" | tee "$OUTPUT_DIR/run.log"

python3 - "$REPORT" "$TIER" <<'PY'
import json
import sys
from pathlib import Path

report_path = Path(sys.argv[1])
tier = sys.argv[2]
report = json.loads(report_path.read_text(encoding="utf-8"))
if report.get("phase") != "complete" or report.get("invariant") is not True:
    raise SystemExit("pending request matrix report did not complete its correctness gates")

cells = report.get("cells") or []
categories = {cell.get("category") for cell in cells}
required = {
    "hard-gate",
    "high-occupancy",
    "sparse-deadline",
    "long-short-mix",
    "overload-recovery",
    "production-profile",
}
missing = sorted(required - categories)
if missing:
    raise SystemExit(f"pending request matrix report is missing categories: {missing}")

profiles = {cell.get("profile") for cell in cells if cell.get("category") == "production-profile"}
for required_profile in ("plain-control", "typical-production"):
    if required_profile not in profiles:
        raise SystemExit(f"missing production profile {required_profile}")
if tier == "p1" and "feature-heavy" not in profiles:
    raise SystemExit("p1 matrix is missing feature-heavy production profile")

if any(cell.get("invariant") is not True for cell in cells):
    raise SystemExit("one or more pending request matrix cells failed their invariant")

print(json.dumps({
    "phase": "validated",
    "tier": tier,
    "cellCount": len(cells),
    "categories": sorted(categories),
    "profiles": sorted(profile for profile in profiles if profile),
    "report": str(report_path),
}, sort_keys=True))
PY
