#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACT_DIR="${1:-$ROOT/artifacts/nuget}"
ARTIFACT_DIR="$(cd "$ARTIFACT_DIR" && pwd)"

shopt -s nullglob
sdk_packages=("$ARTIFACT_DIR"/SharpLink.Sdk.*.nupkg)
if [[ ${#sdk_packages[@]} -ne 1 ]]; then
  echo "Expected exactly one SharpLink.Sdk nupkg in $ARTIFACT_DIR." >&2
  exit 1
fi
package_name="$(basename "${sdk_packages[0]}")"
package_version="${package_name#SharpLink.Sdk.}"
package_version="${package_version%.nupkg}"

work_dir="$(mktemp -d "${TMPDIR:-/tmp}/sharplink-quickstart-package-smoke.XXXXXX")"
server_pid=""
cleanup() {
  if [[ -n "$server_pid" ]] && kill -0 "$server_pid" 2>/dev/null; then
    kill -TERM "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -rf "$work_dir"
}
trap cleanup EXIT

cp -R "$ROOT/samples/QuickStart.Contracts" "$work_dir/QuickStart.Contracts"
cp -R "$ROOT/samples/QuickStart.Server" "$work_dir/QuickStart.Server"
cp -R "$ROOT/samples/QuickStart.Client" "$work_dir/QuickStart.Client"

cat >"$work_dir/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="SharpLink local packages" value="$ARTIFACT_DIR" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
EOF

export NUGET_PACKAGES="$work_dir/.nuget-packages"
package_args=(
  -p:UseSharpLinkPackages=true
  -p:SharpLinkPackageVersion="$package_version"
)

restore_and_build() {
  local project="$1"
  dotnet restore "$project" \
    --force \
    --no-cache \
    --configfile "$work_dir/NuGet.config" \
    "${package_args[@]}"
  dotnet build "$project" \
    -c Release \
    --no-restore \
    -v minimal \
    "${package_args[@]}"
}

restore_and_build "$work_dir/QuickStart.Contracts/QuickStart.Contracts.csproj"
restore_and_build "$work_dir/QuickStart.Server/QuickStart.Server.csproj"
restore_and_build "$work_dir/QuickStart.Client/QuickStart.Client.csproj"

if grep -F '"SharpLink.Runtime/' "$work_dir/QuickStart.Contracts/obj/project.assets.json" >/dev/null; then
  echo "QuickStart.Contracts unexpectedly restored SharpLink.Runtime." >&2
  exit 1
fi

if [[ -z "$(find "$NUGET_PACKAGES/sharplink.sdk" -path '*/analyzers/dotnet/cs/SharpLink.Generator.dll' -print -quit 2>/dev/null)" ]]; then
  echo "SharpLink.Sdk package did not provide SharpLink.Generator.dll as an analyzer." >&2
  exit 1
fi

server_log="$work_dir/server.log"
dotnet run \
  -c Release \
  --no-build \
  --no-restore \
  --project "$work_dir/QuickStart.Server/QuickStart.Server.csproj" \
  "${package_args[@]}" \
  -- --once >"$server_log" 2>&1 &
server_pid=$!

ready=false
for _ in {1..100}; do
  if grep -F 'QUICKSTART_SERVER_READY' "$server_log" >/dev/null 2>&1; then
    ready=true
    break
  fi
  if ! kill -0 "$server_pid" 2>/dev/null; then
    cat "$server_log" >&2
    echo "Quick Start server exited before becoming ready." >&2
    exit 1
  fi
  sleep 0.1
done
if [[ "$ready" != true ]]; then
  cat "$server_log" >&2
  echo "Timed out waiting for Quick Start server readiness." >&2
  exit 1
fi

client_output="$(dotnet run \
  -c Release \
  --no-build \
  --no-restore \
  --project "$work_dir/QuickStart.Client/QuickStart.Client.csproj" \
  "${package_args[@]}")"
printf '%s\n' "$client_output"
printf '%s\n' "$client_output" | grep -F 'QUICKSTART_CLIENT_PASS response=Hello, SharpLink!' >/dev/null

stopped=false
for _ in {1..100}; do
  if ! kill -0 "$server_pid" 2>/dev/null; then
    stopped=true
    break
  fi
  sleep 0.1
done
if [[ "$stopped" != true ]]; then
  cat "$server_log" >&2
  echo "Quick Start server did not complete graceful shutdown after the smoke RPC." >&2
  exit 1
fi
wait "$server_pid"
server_pid=""
cat "$server_log"
grep -F 'QUICKSTART_SERVER_STOPPED' "$server_log" >/dev/null

echo "QUICKSTART_PACKAGE_SMOKE_PASS version=$package_version"
