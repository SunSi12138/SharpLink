#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG="$ROOT/test/SharpLink.PackageSmoke/NuGet.config"
ARTIFACT_ROOT="$ROOT/artifacts/generated-abi-mixing"
PACKAGE_CACHE="$ARTIFACT_ROOT/packages"

if [[ ! -f "$ROOT/artifacts/nuget/SharpLink.Sdk.2.0.0.nupkg" ]] ||
   [[ ! -f "$ROOT/artifacts/nuget/SharpLink.Abstractions.2.0.0.nupkg" ]]; then
  echo "Pack SharpLink 2.0.0 into artifacts/nuget before running the ABI mixing gate." >&2
  exit 2
fi

rm -rf "$ARTIFACT_ROOT"
mkdir -p "$ARTIFACT_ROOT" "$PACKAGE_CACHE"

verify_rejected() {
  local name="$1"
  local project="$2"
  local assembly="$3"
  local log="$ARTIFACT_ROOT/$name.log"
  local project_directory
  project_directory="$(dirname "$project")"

  rm -rf "$project_directory/bin" "$project_directory/obj"
  if NUGET_PACKAGES="$PACKAGE_CACHE" dotnet restore "$project" \
      --force --no-cache --configfile "$CONFIG" >"$log" 2>&1; then
    if NUGET_PACKAGES="$PACKAGE_CACHE" dotnet build "$project" \
        -c Release --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false \
        >>"$log" 2>&1; then
      echo "$name unexpectedly restored and compiled." >&2
      return 1
    fi
  fi

  if [[ -f "$project_directory/bin/Release/net10.0/$assembly.dll" ]]; then
    echo "$name produced an assembly despite the incompatible package graph." >&2
    return 1
  fi
  if ! grep -Eiq \
      "NU1605|downgrade|version conflict|IRpcStub|Invoke(NoReturn)?(Cancellable)?Async|SharpLinkGeneratedContractDescriptor|could not be found|does not exist" \
      "$log"; then
    echo "$name failed without an explicit package or generated-ABI diagnostic." >&2
    tail -n 40 "$log" >&2
    return 1
  fi
}

verify_rejected \
  new-generator-old-abstractions \
  "$ROOT/test/fixtures/generated-abi-mixing/new-generator-old-abstractions/NewGeneratorOldAbstractions.csproj" \
  SharpLink.NewGeneratorOldAbstractions

verify_rejected \
  old-generator-new-abstractions \
  "$ROOT/test/fixtures/generated-abi-mixing/old-generator-new-abstractions/OldGeneratorNewAbstractions.csproj" \
  SharpLink.OldGeneratorNewAbstractions

echo "Generated ABI package-mixing gate passed: both unsupported graphs were rejected without output assemblies."
