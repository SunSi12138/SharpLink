#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == *.dll ]]; then
  : "${ISSUE739_RUNTIME_VERSION:?Set the exact target runtime version}"
  exec dotnet exec --fx-version "$ISSUE739_RUNTIME_VERSION" "$@"
fi
exec dotnet "$@"
