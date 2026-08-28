#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${1:-$repo_root/artifacts/maintainability}"
requested_source_ref="${SHARPLINK_MAINTAINABILITY_SOURCE_REF:-}"
pinned_tool_ref="${SHARPLINK_MAINTAINABILITY_PINNED_TOOL_REF:-}"
scan_root="$repo_root"
temp_parent=""
temp_worktree=""

case "$output_dir" in
  /*) ;;
  *) output_dir="$repo_root/$output_dir" ;;
esac

cleanup() {
  if [[ -n "$temp_worktree" ]]; then
    git -C "$repo_root" worktree remove --force "$temp_worktree" >/dev/null 2>&1 || true
  fi
  if [[ -n "$temp_parent" ]]; then
    rm -rf "$temp_parent"
  fi
}
trap cleanup EXIT

if [[ -n "$requested_source_ref" && -z "$pinned_tool_ref" ]]; then
  tool_ref="$(git -C "$repo_root" log -1 --format=%H -- \
    eng/report-maintainability.sh \
    eng/SharpLink.Maintainability \
    Directory.Build.props \
    Directory.Build.targets \
    Directory.Packages.props \
    global.json \
    NuGet.config \
    NuGet.Config)"
  if [[ -z "$tool_ref" ]]; then
    echo "Unable to resolve maintainability tool revision." >&2
    exit 2
  fi

  temp_parent="$(mktemp -d "${TMPDIR:-/tmp}/sharplink-maintainability-tool.XXXXXX")"
  temp_worktree="$temp_parent/tool"
  git -C "$repo_root" worktree add --detach "$temp_worktree" "$tool_ref" >/dev/null
  git -C "$temp_worktree" sparse-checkout disable >/dev/null
  git -C "$temp_worktree" reset --hard "$tool_ref" >/dev/null

  set +e
  SHARPLINK_MAINTAINABILITY_PINNED_TOOL_REF="$tool_ref" \
    SHARPLINK_MAINTAINABILITY_SOURCE_REF="$requested_source_ref" \
    bash "$temp_worktree/eng/report-maintainability.sh" "$output_dir"
  status=$?
  set -e
  exit "$status"
fi

if [[ -n "$requested_source_ref" ]]; then
  if ! source_ref="$(git -C "$repo_root" rev-parse --verify "${requested_source_ref}^{commit}" 2>/dev/null)"; then
    echo "Unable to resolve maintainability source ref: $requested_source_ref" >&2
    exit 2
  fi
  if [[ -z "$pinned_tool_ref" ]]; then
    echo "Named snapshot requires a pinned maintainability tool revision." >&2
    exit 2
  fi

  temp_parent="$(mktemp -d "${TMPDIR:-/tmp}/sharplink-maintainability-source.XXXXXX")"
  temp_worktree="$temp_parent/source"
  git -C "$repo_root" worktree add --detach "$temp_worktree" "$source_ref" >/dev/null
  git -C "$temp_worktree" sparse-checkout disable >/dev/null
  git -C "$temp_worktree" reset --hard "$source_ref" >/dev/null
  scan_root="$temp_worktree"
  tool_ref="$pinned_tool_ref"
else
  source_ref="working-tree"
  tool_ref="working-tree"
fi

args=(--root "$scan_root" --output "$output_dir" --source-ref "$source_ref" --tool-ref "$tool_ref")

dotnet run \
  --project "$repo_root/eng/SharpLink.Maintainability/SharpLink.Maintainability.csproj" \
  --configuration Release \
  -- "${args[@]}"
