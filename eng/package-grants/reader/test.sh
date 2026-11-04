#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet="${DOTNET_HOST_PATH:-dotnet}"
dotnet="$(command -v "$dotnet")"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$root/.cache/dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$root/.cache/nuget}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
for project in SharpLink.AssemblyGrants tests/fixtures/Grants tests/fixtures/NoGrants tests; do
  "$dotnet" build "$root/$project" -c Release --nologo -v:q \
    -p:UseSharedCompilation=false -m:1 -nr:false >&2
done
"$dotnet" "$root/tests/bin/Release/net10.0/SharpLink.AssemblyGrants.Tests.dll" \
  "$dotnet" "$root/SharpLink.AssemblyGrants/bin/Release/net10.0/SharpLink.AssemblyGrants.dll" "$root"
