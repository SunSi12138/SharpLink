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
  -c Release -r "$RID" -p:PublishAot=true -o "$OUTPUT" -v minimal

EXE="$OUTPUT/SharpLink.AotSmoke"
if [[ "$RID" == win-* ]]; then
  EXE="$EXE.exe"
fi

SERVER_LOG="$OUTPUT/server.log"
CLIENT_LOG="$OUTPUT/client.log"
COMPLETION_FILE="$OUTPUT/client-complete-$$"
"$EXE" sharedmemory --role server --shm-name "$NAME" \
  --completion-file "$COMPLETION_FILE" >"$SERVER_LOG" 2>&1 &
SERVER_PID=$!
cleanup() {
  rm -f "$COMPLETION_FILE"
  if kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

"$EXE" sharedmemory --role client --shm-name "$NAME" | tee "$CLIENT_LOG"
: >"$COMPLETION_FILE"
wait "$SERVER_PID"
grep -q "REFERENCED_SERVICE_PASS" "$CLIENT_LOG"
grep -q "AOT_SMOKE_CLIENT_PASS" "$CLIENT_LOG"
grep -q "AOT_SMOKE_SERVER_PASS" "$SERVER_LOG"
rm -f "$COMPLETION_FILE"
trap - EXIT

LOCAL_LOG="$OUTPUT/local-topologies.log"
"$EXE" | tee "$LOCAL_LOG"
grep -q "STATIC_READINESS_PASS" "$LOCAL_LOG"
grep -q "AOT_SMOKE_PASS transport=tcp" "$LOCAL_LOG"

SIDECAR_OUTPUT="$OUTPUT/sharppack-sidecar"
mkdir -p "$SIDECAR_OUTPUT"
dotnet publish "$ROOT/test/SharpLink.SharpPackAotSmoke/SharpLink.SharpPackAotSmoke.csproj" \
  -c Release -r "$RID" -p:PublishAot=true -o "$SIDECAR_OUTPUT" -v minimal

SIDECAR_EXE="$SIDECAR_OUTPUT/SharpLink.SharpPackAotSmoke"
if [[ "$RID" == win-* ]]; then
  SIDECAR_EXE="$SIDECAR_EXE.exe"
fi

SIDECAR_LOG="$SIDECAR_OUTPUT/smoke.log"
"$SIDECAR_EXE" | tee "$SIDECAR_LOG"
grep -q "SHARPPACK_SIDECAR_AOT_PASS" "$SIDECAR_LOG"

PRECREDIT_OUTPUT="$OUTPUT/precredit"
mkdir -p "$PRECREDIT_OUTPUT"
dotnet publish "$ROOT/test/SharpLink.PreCreditAotSmoke/SharpLink.PreCreditAotSmoke.csproj" \
  -c Release -r "$RID" -p:PublishAot=true -o "$PRECREDIT_OUTPUT" -v minimal

PRECREDIT_EXE="$PRECREDIT_OUTPUT/SharpLink.PreCreditAotSmoke"
if [[ "$RID" == win-* ]]; then
  PRECREDIT_EXE="$PRECREDIT_EXE.exe"
fi

PRECREDIT_TCP_LOG="$PRECREDIT_OUTPUT/tcp.log"
"$PRECREDIT_EXE" tcp | tee "$PRECREDIT_TCP_LOG"
grep -q "PRE_CREDIT_AOT_PASS transport=tcp" "$PRECREDIT_TCP_LOG"

PRECREDIT_SHM_LOG="$PRECREDIT_OUTPUT/sharedmemory.log"
"$PRECREDIT_EXE" sharedmemory | tee "$PRECREDIT_SHM_LOG"
grep -q "PRE_CREDIT_AOT_PASS transport=sharedmemory" "$PRECREDIT_SHM_LOG"

echo "Shared-memory process, local endpoint-topology, SharpPack sidecar, and pre-credit NativeAOT smokes passed ($RID)."
