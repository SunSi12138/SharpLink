#!/usr/bin/env bash
# Preserve compilation logs, every emitted result, and raw native/managed errors.
set -uo pipefail
if [ "$#" -ne 3 ]; then
  printf 'Usage: %s <024-round-3.dmp> <matching-libmscordaccore.so> <output-dir>\n' "$0" >&2
  exit 64
fi
HERE=$(cd -- "$(dirname -- "$0")" && pwd)
DUMP=$(realpath -- "$1")
DAC=$(realpath -- "$2")
OUT=$(realpath -m -- "$3")
mkdir -p -- "$OUT"
sha256sum -- "$DUMP" "$DAC" > "$OUT/input-sha256.txt"
# Disable core creation for the diagnostic tool itself: native DAC errors must not
# produce and accidentally upload a second dump of the analyzer's own memory.
ulimit -c 0
set +e
dotnet build "$HERE/ManagedAbiDumpReader.csproj" -c Release --nologo > "$OUT/build.stdout.log" 2> "$OUT/build.stderr.log"
BUILD=$?
printf '%s\n' "$BUILD" > "$OUT/build.exit-code.txt"
if [ "$BUILD" -ne 0 ]; then exit "$BUILD"; fi
# A DAC can still crash or hang. Missing terminal JSON summary is never success.
timeout --signal=TERM --kill-after=10s 180s dotnet "$HERE/bin/Release/net10.0/ManagedAbiDumpReader.dll" "$DUMP" "$DAC" > "$OUT/reader.jsonl" 2> "$OUT/reader.stderr.log"
STATUS=$?
printf '%s\n' "$STATUS" > "$OUT/reader.exit-code.txt"
python3 - "$OUT" "$STATUS" <<'PY'
import json,pathlib,sys
out=pathlib.Path(sys.argv[1]);status=int(sys.argv[2]);summary=None;parse_errors=[]
for index,line in enumerate((out/'reader.jsonl').read_text(errors='replace').splitlines(),1):
    try:
        item=json.loads(line)
        if item.get('kind')=='summary': summary=item
    except Exception as exc: parse_errors.append({'line':index,'error':str(exc)})
complete=status==0 and summary is not None and summary.get('status')=='complete-within-declared-scope' and not parse_errors
(out/'run-summary.json').write_text(json.dumps({'processExitCode':status,'readerSummary':summary,'jsonParseErrors':parse_errors,'complete':complete,'readOnly':True},indent=2)+'\n')
if not complete: print('Reader failed or produced partial results; inspect raw logs and run-summary.json.',file=sys.stderr)
sys.exit(0 if complete else 2)
PY
VERIFY=$?
if [ "$STATUS" -ne 0 ]; then exit "$STATUS"; fi
exit "$VERIFY"
