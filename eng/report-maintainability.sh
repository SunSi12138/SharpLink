#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${1:-$repo_root/artifacts/maintainability}"
requested_source_ref="${SHARPLINK_MAINTAINABILITY_SOURCE_REF:-}"

untracked_source_test_files() {
  git -C "$repo_root" ls-files --others --exclude-standard -- src test
}

if [[ -n "$requested_source_ref" ]]; then
  if ! source_ref="$(git -C "$repo_root" rev-parse --verify "${requested_source_ref}^{commit}" 2>/dev/null)"; then
    echo "Unable to resolve maintainability source ref: $requested_source_ref" >&2
    exit 2
  fi

  if ! git -C "$repo_root" diff --quiet "$source_ref" -- src test \
    || [[ -n "$(untracked_source_test_files)" ]]; then
    echo "src/ and test/ do not match requested source ref: $source_ref" >&2
    echo "Generate the named snapshot from matching source/test contents, or omit SHARPLINK_MAINTAINABILITY_SOURCE_REF." >&2
    exit 2
  fi
else
  head_ref="$(git -C "$repo_root" rev-parse --verify HEAD 2>/dev/null || true)"
  if [[ -n "$head_ref" ]] \
    && git -C "$repo_root" diff --quiet "$head_ref" -- src test \
    && [[ -z "$(untracked_source_test_files)" ]]; then
    source_ref="$head_ref"
  else
    source_ref="working-tree"
  fi
fi

args=(--root "$repo_root" --output "$output_dir" --source-ref "$source_ref")

dotnet run \
  --project "$repo_root/eng/SharpLink.Maintainability/SharpLink.Maintainability.csproj" \
  --configuration Release \
  -- "${args[@]}"

report_md="$output_dir/report.md"
temp_report_md="$report_md.tmp"
awk '
  /^## Large methods \(/ { sub(/^## Large methods /, "## Top 25 large methods ") }
  /^## Complex methods \(/ { sub(/^## Complex methods /, "## Top 25 complex methods ") }
  { print }
' "$report_md" > "$temp_report_md"
mv "$temp_report_md" "$report_md"
