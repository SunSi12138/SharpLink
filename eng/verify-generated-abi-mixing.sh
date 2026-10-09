#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACT_ROOT="$ROOT/artifacts/generated-abi-mixing"
CONFIG="$ARTIFACT_ROOT/NuGet.config"
PACKAGE_CACHE="$ARTIFACT_ROOT/packages"
current_version="$(python3 -c 'import sys, xml.etree.ElementTree as ET; print(ET.parse(sys.argv[1]).findtext(".//VersionPrefix"))' "$ROOT/Directory.Build.props")"

if [[ ! -f "$ROOT/artifacts/nuget/SharpLink.Sdk.$current_version.nupkg" ]] ||
   [[ ! -f "$ROOT/artifacts/nuget/SharpLink.Abstractions.$current_version.nupkg" ]]; then
  echo "Pack SharpLink $current_version into artifacts/nuget before running the ABI mixing gate." >&2
  exit 2
fi

rm -rf "$ARTIFACT_ROOT"
mkdir -p "$ARTIFACT_ROOT" "$PACKAGE_CACHE"

# This negative-compatibility gate intentionally mixes one exact current SharpLink package
# from the just-built local feed with one exact published 1.1.1 package from nuget.org.
# Do not reuse PackageSmoke's source-mapped config: that correctly pins all SharpLink.*
# packages to the local feed and would therefore make the old-version half unrestorable.
cat >"$CONFIG" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="SharpLink current packages" value="$ROOT/artifacts/nuget" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
EOF

verify_rejected() {
  local name="$1"
  local project="$2"
  local assembly="$3"
  local log="$ARTIFACT_ROOT/$name.log"
  local project_directory
  project_directory="$(dirname "$project")"

  rm -rf "$project_directory/bin" "$project_directory/obj"
  if NUGET_PACKAGES="$PACKAGE_CACHE" dotnet restore "$project" \
      --force --no-cache --configfile "$CONFIG" \
      -p:SharpLinkCurrentVersion="$current_version" >"$log" 2>&1; then
    if NUGET_PACKAGES="$PACKAGE_CACHE" dotnet build "$project" \
        -c Release --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false \
        -p:SharpLinkCurrentVersion="$current_version" \
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
