#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${1:-$repo_root/artifacts/maintainability}"
python_bin="${PYTHON:-python3}"

case "$output_dir" in
  /*) ;;
  *) output_dir="$repo_root/$output_dir" ;;
esac

bash "$repo_root/eng/report-maintainability.sh" "$output_dir"

"$python_bin" "$repo_root/eng/verify-maintainability.py" \
  --report "$output_dir/report.json" \
  --baseline "$repo_root/eng/maintainability/baseline.json"
