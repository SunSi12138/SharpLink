#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/test/fixtures/protocol-v2-cross-version/SharpLink.ProtocolV2CrossVersion.csproj"
CONFIG="$ROOT/test/SharpLink.PackageSmoke/NuGet.config"
ARTIFACT_ROOT="$ROOT/artifacts/protocol-v2-cross-version"
PACKAGE_CACHE="$ARTIFACT_ROOT/packages"
ACTIVE_SERVER_PID=""

cleanup_server() {
  if [[ -n "$ACTIVE_SERVER_PID" ]] && kill -0 "$ACTIVE_SERVER_PID" 2>/dev/null; then
    kill "$ACTIVE_SERVER_PID" 2>/dev/null || true
  fi
}
trap cleanup_server EXIT

if [[ ! -f "$ROOT/artifacts/nuget/SharpLink.Sdk.2.0.0.nupkg" ]]; then
  echo "Pack SharpLink 2.0.0 into artifacts/nuget before running the Protocol v2 process gate." >&2
  exit 2
fi

rm -rf "$ARTIFACT_ROOT"
mkdir -p "$ARTIFACT_ROOT" "$PACKAGE_CACHE"

NUGET_PACKAGES="$PACKAGE_CACHE" dotnet restore "$PROJECT" \
  --force --no-cache --configfile "$CONFIG" \
  -p:SharpLinkVersion=2.0.0 \
  -p:BaseIntermediateOutputPath="$ARTIFACT_ROOT/v200-obj/"
NUGET_PACKAGES="$PACKAGE_CACHE" dotnet build "$PROJECT" \
  -c Release --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false \
  -p:SharpLinkVersion=2.0.0 \
  -p:BaseIntermediateOutputPath="$ARTIFACT_ROOT/v200-obj/" \
  -p:OutputPath="$ARTIFACT_ROOT/v200-bin/"

server_dll="$ARTIFACT_ROOT/v200-bin/SharpLink.ProtocolV2CrossVersion.dll"
client_dll="$ARTIFACT_ROOT/v200-bin/SharpLink.ProtocolV2CrossVersion.dll"
server_log="$ARTIFACT_ROOT/v200-server.log"
client_log="$ARTIFACT_ROOT/v200-client.log"

dotnet "$server_dll" server >"$server_log" 2>&1 &
ACTIVE_SERVER_PID=$!

port=""
for _ in $(seq 1 200); do
  port="$(sed -n 's/^SERVER_READY //p' "$server_log" | head -n 1)"
  if [[ -n "$port" ]]; then
    break
  fi
  if ! kill -0 "$ACTIVE_SERVER_PID" 2>/dev/null; then
    break
  fi
  sleep 0.05
done
if [[ -z "$port" ]]; then
  echo "SharpLink 2.0 server did not publish its bound endpoint." >&2
  tail -n 40 "$server_log" >&2
  exit 1
fi

dotnet "$client_dll" client "$port" >"$client_log" 2>&1
wait "$ACTIVE_SERVER_PID"
ACTIVE_SERVER_PID=""
grep -Fx "CLIENT_PASS" "$client_log" >/dev/null
grep -Fx "SERVER_PASS" "$server_log" >/dev/null

echo "Protocol v2 process gate passed for the SharpLink 2.0 package set. Pre-2.0 cross-version compatibility is intentionally out of scope."
