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
  local project_directory
  project_directory="$(dirname "$project")"
  local restore_log="$ARTIFACT_ROOT/$name.restore.log"
  local build_log="$ARTIFACT_ROOT/$name.build.log"
  local validator="$ROOT/eng/validate-generated-abi-mixing.py"

  # Before accepting a *negative* outcome, prove the intended source and exact
  # 1.1.1/current package graph exist (not a typo, missing file or unrelated build).
  python3 "$validator" fixture "$project" "$name" || return 1
  rm -rf "$project_directory/bin" "$project_directory/obj"

  # Each negative must have a corresponding same-version positive control:
  # this catches malformed source, missing workload, and fixture-only failures.
  local positive_dir="$ARTIFACT_ROOT/positive/$name"
  python3 "$validator" positive "$project" "$positive_dir" || return 1
  local positive_project="$positive_dir/$(basename "$project")"
  if ! NUGET_PACKAGES="$PACKAGE_CACHE" dotnet restore "$positive_project" \
      --force --no-cache --configfile "$CONFIG" \
      -p:SharpLinkCurrentVersion="$current_version" \
      >"$ARTIFACT_ROOT/$name.positive-restore.log" 2>&1 ||
     ! NUGET_PACKAGES="$PACKAGE_CACHE" dotnet build "$positive_project" \
      -c Release --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false \
      -p:SharpLinkCurrentVersion="$current_version" \
      >"$ARTIFACT_ROOT/$name.positive-build.log" 2>&1; then
    echo "$name: same-version positive control failed, so the negative is inconclusive." >&2
    return 1
  fi
  if [[ ! -f "$positive_dir/bin/Release/net10.0/$assembly.dll" ]]; then
    echo "$name: positive control did not produce its expected assembly." >&2
    return 1
  fi

  local phase
  if ! NUGET_PACKAGES="$PACKAGE_CACHE" dotnet restore "$project" \
      --force --no-cache --configfile "$CONFIG" \
      -p:SharpLinkCurrentVersion="$current_version" >"$restore_log" 2>&1; then
    phase=restore
  elif ! NUGET_PACKAGES="$PACKAGE_CACHE" dotnet build "$project" \
      -c Release --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false \
      -p:SharpLinkCurrentVersion="$current_version" >"$build_log" 2>&1; then
    phase=build
  else
    echo "$name unexpectedly restored and compiled." >&2
    return 1
  fi

  if [[ -f "$project_directory/bin/Release/net10.0/$assembly.dll" ]]; then
    echo "$name produced an assembly despite the incompatible package graph." >&2
    return 1
  fi
  local error_log="$restore_log"
  [[ "$phase" == build ]] && error_log="$build_log"
  if ! python3 "$validator" classify "$phase" "$error_log"; then
    echo "$name: $phase failed without a valid SharpLink version/ABI diagnostic." >&2
    tail -n 40 "$error_log" >&2
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
