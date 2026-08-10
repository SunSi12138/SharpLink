#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACT_DIR="${1:-$ROOT/artifacts/nuget}"
ARTIFACT_DIR="$(cd "$ARTIFACT_DIR" && pwd)"
PROJECT="$ROOT/test/SharpLink.AbstractionsPackageSmoke/SharpLink.AbstractionsPackageSmoke.csproj"
ASSETS="$ROOT/test/SharpLink.AbstractionsPackageSmoke/obj/project.assets.json"

abstractions_packages=("$ARTIFACT_DIR"/SharpLink.Abstractions.*.nupkg)
if [[ ${#abstractions_packages[@]} -ne 1 || ! -f "${abstractions_packages[0]}" ]]; then
  echo "Expected exactly one SharpLink.Abstractions nupkg in $ARTIFACT_DIR." >&2
  exit 1
fi

abstractions_package="${abstractions_packages[0]}"
version="${abstractions_package#"$ARTIFACT_DIR/SharpLink.Abstractions."}"
version="${version%.nupkg}"
if unzip -p "$abstractions_package" SharpLink.Abstractions.nuspec |
   grep -F '<dependency id="Microsoft.Extensions.DependencyInjection.Abstractions"' >/dev/null; then
  echo "SharpLink.Abstractions must not depend on Microsoft.Extensions.DependencyInjection.Abstractions." >&2
  exit 1
fi

package_cache="$(mktemp -d)"
trap 'rm -rf -- "$package_cache"' EXIT

NUGET_PACKAGES="$package_cache" dotnet restore "$PROJECT" \
  --force \
  --no-cache \
  --source "$ARTIFACT_DIR" \
  --source https://api.nuget.org/v3/index.json \
  -p:SharpLinkPackageVersion="$version"
if grep -Fi 'Microsoft.Extensions.DependencyInjection.Abstractions/' "$ASSETS" >/dev/null; then
  echo "The Abstractions-only package graph still contains Microsoft.Extensions.DependencyInjection.Abstractions." >&2
  exit 1
fi

NUGET_PACKAGES="$package_cache" dotnet run \
  --configuration Release \
  --no-restore \
  --project "$PROJECT" \
  -p:SharpLinkPackageVersion="$version"
