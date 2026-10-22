#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")" && pwd)"
cd "$root"
[[ "$(dotnet --version)" == 10.0.102 ]]
mkdir -p "$root/evidence"
dotnet --version > "$root/evidence/build-sdk-version.txt"
for arm in "$@"; do
  enabled=false
  if [[ "$arm" == candidate* ]]; then enabled=true; fi
  mkdir -p "$root/evidence/$arm"
  dotnet build "$root/probe/HotCostProbe.csproj" -c Release -m:1 -p:UseSharedCompilation=false \
    -p:SubjectRoot="$root/arms/$arm" -p:EnableMaintenance="$enabled" --artifacts-path "$root/evidence/$arm/build"
done
