#!/usr/bin/env bash
set -euo pipefail
out="$(realpath -m "$1")"
tooling="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_root="$(git rev-parse --show-toplevel)"
case "$out/" in "$source_root/"*) echo 'Evidence must be outside production checkout.' >&2; exit 2 ;; esac
mkdir -p "$out"
test "$(git rev-parse HEAD)" = "$SHARPLINK_COMMIT"
test -z "$(git status --porcelain --untracked-files=normal)"
bash "$tooling/run-release-204-dll-reader.sh" "$source_root" "$SHARPLINK_COMMIT" "$out/dll-identities.json" 2> "$out/dll-inspector-build.log"
git rev-parse HEAD > "$out/source-sha.txt"
python3 - "$out" "$tooling" <<'PYTHON'
import os, pathlib, sys
sys.path.insert(0, sys.argv[2])
from release_204_evidence import expected_script_hashes, read_json, validate_provenance
out = pathlib.Path(sys.argv[1])
items = read_json(out / 'dll-identities.json')['Assemblies']
(out / 'dll-sha256.txt').write_text(''.join(f'{row["Sha256"]}  {row["Path"]}\n' for row in items))
context = dict(SourceCommit=os.environ['SHARPLINK_COMMIT'], ToolingCommit=os.environ['GITHUB_SHA'],
               RunId=os.environ['GITHUB_RUN_ID'], RunAttempt=os.environ['GITHUB_RUN_ATTEMPT'], FinalMain=os.environ['FINAL_MAIN'] == 'true')
failures = validate_provenance(out, context, expected_script_hashes(pathlib.Path.cwd(), context['SourceCommit']), os.environ['REQUIRED_BINARIES'].split(','))
if failures:
    raise SystemExit('\n'.join(failures))
PYTHON
test "$(git rev-parse HEAD)" = "$SHARPLINK_COMMIT"
test -z "$(git status --porcelain --untracked-files=normal)"
