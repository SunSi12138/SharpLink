#!/usr/bin/env bash
set -euo pipefail
# Run in the immutable production checkout. Output must be outside the checkout.
out="$(realpath -m "$1")"
tooling="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_root="$(git rev-parse --show-toplevel)"
case "$out/" in "$source_root/"*) echo 'Evidence must be outside production checkout.' >&2; exit 2 ;; esac
mkdir -p "$out"
[[ "$SHARPLINK_COMMIT" =~ ^[0-9a-f]{40}$ ]]
[[ "$GITHUB_SHA" =~ ^[0-9a-f]{40}$ ]]
[[ "$FINAL_MAIN" == true || "$FINAL_MAIN" == false ]]
[[ "$GITHUB_RUN_ID" =~ ^[1-9][0-9]*$ && "$GITHUB_RUN_ATTEMPT" =~ ^[1-9][0-9]*$ ]]
test "$(git rev-parse HEAD)" = "$SHARPLINK_COMMIT"
test -z "$(git status --porcelain --untracked-files=normal)"
test "$(git -C "$tooling" rev-parse HEAD)" = "$GITHUB_SHA"
test -z "$(git -C "$tooling" status --porcelain --untracked-files=normal)"
python3 - <<'PYTHON'
import xml.etree.ElementTree as ET
root = ET.parse("Directory.Build.props").getroot()
assert root.findtext(".//VersionPrefix") == "2.0.4", "This limited exception is only for 2.0.4"
assert not root.findtext(".//VersionSuffix"), "Stable release expected"
PYTHON
if [[ "$FINAL_MAIN" == true ]]; then
  test "$(git rev-parse refs/remotes/origin/main)" = "$SHARPLINK_COMMIT"
fi
{
  printf 'source_commit=%s\nworkflow_commit=%s\nrun_id=%s\nrun_attempt=%s\nfinal_main=%s\n' "$SHARPLINK_COMMIT" "$GITHUB_SHA" "$GITHUB_RUN_ID" "$GITHUB_RUN_ATTEMPT" "$FINAL_MAIN"
  git status --porcelain --untracked-files=normal
  date -u +%FT%TZ
  uname -a
  lscpu
  dotnet --info
} > "$out/environment.txt"
python3 - "$out" "$tooling" <<'PYTHON'
import json, os, pathlib, subprocess, sys
sys.path.insert(0, str(pathlib.Path(sys.argv[2]) / 'eng'))
from release_204_evidence import expected_script_hashes
out = pathlib.Path(sys.argv[1])
sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
data = dict(SchemaVersion=1, SourceCommit=sha, ToolingCommit=subprocess.check_output(['git', '-C', sys.argv[2], 'rev-parse', 'HEAD'], text=True).strip(),
            RunId=os.environ['GITHUB_RUN_ID'], RunAttempt=os.environ['GITHUB_RUN_ATTEMPT'], FinalMain=os.environ['FINAL_MAIN'] == 'true',
            MainCommit=subprocess.check_output(['git', 'rev-parse', 'refs/remotes/origin/main'], text=True).strip(),
            SourceTreeClean=True, Version='2.0.4')
(out / 'provenance.json').write_text(json.dumps(data, indent=2) + '\n')
hashes = expected_script_hashes(pathlib.Path.cwd(), sha)
(out / 'canonical-scripts.sha256').write_text(''.join(f'{value}  {name}\n' for name, value in sorted(hashes.items())))
PYTHON
