#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bash "$repo_root/eng/verify-runtime-construction-boundary.sh"
python3 "$repo_root/eng/verify-runtime-architecture.py"
