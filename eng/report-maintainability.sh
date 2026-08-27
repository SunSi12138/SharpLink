#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${1:-$repo_root/artifacts/maintainability}"
source_ref="${SHARPLINK_MAINTAINABILITY_SOURCE_REF:-}"

args=(--root "$repo_root" --output "$output_dir")
if [[ -n "$source_ref" ]]; then
  args+=(--source-ref "$source_ref")
fi

dotnet run \
  --project "$repo_root/eng/SharpLink.Maintainability/SharpLink.Maintainability.csproj" \
  --configuration Release \
  -- "${args[@]}"
