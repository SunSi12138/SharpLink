#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${SHARPLINK_AOT_RID:-}"
OUTPUT="${SHARPLINK_AOT_OUTPUT:-$ROOT/artifacts/aot-shared-memory}"
NAME="${SHARPLINK_AOT_SHM_NAME:-sharplink-aot-process-smoke}"

if [[ -z "$RID" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) RID=linux-x64 ;;
    Darwin-arm64) RID=osx-arm64 ;;
    MINGW*|MSYS*|CYGWIN*) RID=win-x64 ;;
    *) echo "Unsupported NativeAOT smoke host: $(uname -s)-$(uname -m)" >&2; exit 2 ;;
  esac
fi

mkdir -p "$OUTPUT"
dotnet publish "$ROOT/test/SharpLink.AotSmoke/SharpLink.AotSmoke.csproj" \
  -c Release -r "$RID" /p:PublishAot=true -o "$OUTPUT" -v minimal

EXE="$OUTPUT/SharpLink.AotSmoke"
if [[ "$RID" == win-* ]]; then
  EXE="$EXE.exe"
fi

SERVER_LOG="$OUTPUT/server.log"
"$EXE" sharedmemory --role server --shm-name "$NAME" >"$SERVER_LOG" 2>&1 &
SERVER_PID=$!
cleanup() {
  if kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

"$EXE" sharedmemory --role client --shm-name "$NAME"
wait "$SERVER_PID"
grep -q "AOT_SMOKE_SERVER_PASS" "$SERVER_LOG"
trap - EXIT

echo "Shared-memory independent-process NativeAOT smoke passed ($RID)."
