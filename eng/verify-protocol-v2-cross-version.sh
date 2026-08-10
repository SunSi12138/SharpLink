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
  echo "Pack SharpLink 2.0.0 into artifacts/nuget before running the Protocol v2 matrix." >&2
  exit 2
fi

rm -rf "$ARTIFACT_ROOT"
mkdir -p "$ARTIFACT_ROOT" "$PACKAGE_CACHE"

build_version() {
  local label="$1"
  local version="$2"
  local intermediate="$ARTIFACT_ROOT/$label-obj/"
  local output="$ARTIFACT_ROOT/$label-bin/"

  NUGET_PACKAGES="$PACKAGE_CACHE" dotnet restore "$PROJECT" \
    --force --no-cache --configfile "$CONFIG" \
    -p:SharpLinkVersion="$version" \
    -p:BaseIntermediateOutputPath="$intermediate"
  NUGET_PACKAGES="$PACKAGE_CACHE" dotnet build "$PROJECT" \
    -c Release --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false \
    -p:SharpLinkVersion="$version" \
    -p:BaseIntermediateOutputPath="$intermediate" \
    -p:OutputPath="$output"
}

run_pair() {
  local server_label="$1"
  local client_label="$2"
  local pair="$server_label-server--$client_label-client"
  local server_dll="$ARTIFACT_ROOT/$server_label-bin/SharpLink.ProtocolV2CrossVersion.dll"
  local client_dll="$ARTIFACT_ROOT/$client_label-bin/SharpLink.ProtocolV2CrossVersion.dll"
  local server_log="$ARTIFACT_ROOT/$pair-server.log"
  local client_log="$ARTIFACT_ROOT/$pair-client.log"

  dotnet "$server_dll" server >"$server_log" 2>&1 &
  local server_pid=$!
  ACTIVE_SERVER_PID="$server_pid"

  local port=""
  for _ in $(seq 1 200); do
    port="$(sed -n 's/^SERVER_READY //p' "$server_log" | head -n 1)"
    if [[ -n "$port" ]]; then
      break
    fi
    if ! kill -0 "$server_pid" 2>/dev/null; then
      break
    fi
    sleep 0.05
  done
  if [[ -z "$port" ]]; then
    echo "$pair server did not publish its bound endpoint." >&2
    tail -n 40 "$server_log" >&2
    return 1
  fi

  dotnet "$client_dll" client "$port" >"$client_log" 2>&1
  wait "$server_pid"
  ACTIVE_SERVER_PID=""
  grep -Fx "CLIENT_PASS" "$client_log" >/dev/null
  grep -Fx "SERVER_PASS" "$server_log" >/dev/null
}

build_version api3 1.1.1
build_version api4 2.0.0

run_pair api3 api3
run_pair api3 api4
run_pair api4 api3
run_pair api4 api4

echo "Protocol v2 cross-version matrix passed: API3/API4 clients and servers succeeded in all four process pairs."
