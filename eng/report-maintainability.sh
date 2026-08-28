#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${1:-$repo_root/artifacts/maintainability}"
requested_source_ref="${SHARPLINK_MAINTAINABILITY_SOURCE_REF:-}"
pinned_tool_ref="${SHARPLINK_MAINTAINABILITY_PINNED_TOOL_REF:-}"
scan_root="$repo_root"
temp_parent=""
temp_worktree=""
tool_paths=(
  eng/report-maintainability.sh
  eng/SharpLink.Maintainability
  Directory.Build.props
  Directory.Build.targets
  Directory.Packages.props
  global.json
  NuGet.config
  NuGet.Config
)

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

file_matches_ref() {
  local ref="$1"
  local path="$2"
  local expected_blob actual_blob

  if ! expected_blob="$(git -C "$repo_root" rev-parse "$ref:$path" 2>/dev/null)"; then
    return 1
  fi
  if ! actual_blob="$(git -C "$repo_root" hash-object "$repo_root/$path" 2>/dev/null)"; then
    return 1
  fi
  [[ "$expected_blob" == "$actual_blob" ]]
}

untracked_tool_inputs() {
  git -C "$repo_root" ls-files -z --others -- eng/SharpLink.Maintainability |
    while IFS= read -r -d '' path; do
      case "/$path/" in
        */[Bb][Ii][Nn]/*|*/[Oo][Bb][Jj]/*) continue ;;
      esac
      printf '1\n'
      break
    done
}

verify_pinned_tool_worktree() {
  local expected_ref="$1"
  local resolved_ref current_head entry path expected_blob actual_blob candidate

  if ! resolved_ref="$(git -C "$repo_root" rev-parse --verify "${expected_ref}^{commit}" 2>/dev/null)"; then
    echo "Unable to resolve pinned maintainability tool ref: $expected_ref" >&2
    return 1
  fi
  current_head="$(git -C "$repo_root" rev-parse --verify HEAD)"
  if [[ "$current_head" != "$resolved_ref" ]]; then
    echo "Pinned maintainability tool ref does not match the executing worktree HEAD." >&2
    return 1
  fi

  while IFS= read -r -d '' entry; do
    path="${entry#*$'\t'}"
    expected_blob="$(git -C "$repo_root" rev-parse "$resolved_ref:$path")"
    if ! actual_blob="$(git -C "$repo_root" hash-object "$repo_root/$path" 2>/dev/null)" \
      || [[ "$actual_blob" != "$expected_blob" ]]; then
      echo "Pinned maintainability tool input does not match $resolved_ref: $path" >&2
      return 1
    fi
  done < <(git -C "$repo_root" ls-tree -r -z "$resolved_ref" -- "${tool_paths[@]}")

  for candidate in "${tool_paths[@]}"; do
    if ! git -C "$repo_root" cat-file -e "$resolved_ref:$candidate" 2>/dev/null \
      && [[ -e "$repo_root/$candidate" ]]; then
      echo "Pinned maintainability tool worktree contains an input absent from $resolved_ref: $candidate" >&2
      return 1
    fi
  done

  if [[ -n "$(untracked_tool_inputs)" ]]; then
    echo "Pinned maintainability tool worktree contains untracked analyzer inputs." >&2
    return 1
  fi

  pinned_tool_ref="$resolved_ref"
}

if [[ -n "$requested_source_ref" && -z "$pinned_tool_ref" ]]; then
  if ! file_matches_ref HEAD eng/report-maintainability.sh; then
    echo "Named snapshots require eng/report-maintainability.sh to match committed HEAD." >&2
    echo "Commit or discard wrapper changes before generating a named snapshot." >&2
    exit 2
  fi

  tool_ref="$(git -C "$repo_root" log -1 --format=%H -- "${tool_paths[@]}")"
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
  if [[ -z "$pinned_tool_ref" ]]; then
    echo "Named snapshot requires a pinned maintainability tool revision." >&2
    exit 2
  fi
  if ! verify_pinned_tool_worktree "$pinned_tool_ref"; then
    exit 2
  fi
  if ! source_ref="$(git -C "$repo_root" rev-parse --verify "${requested_source_ref}^{commit}" 2>/dev/null)"; then
    echo "Unable to resolve maintainability source ref: $requested_source_ref" >&2
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
