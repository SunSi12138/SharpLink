#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACT_DIR="${1:-$ROOT/artifacts/nuget}"
ARTIFACT_DIR="$(cd "$ARTIFACT_DIR" && pwd)"
PROJECT="$ROOT/test/SharpLink.HostingPackageSmoke/SharpLink.HostingPackageSmoke.csproj"

hosting_packages=("$ARTIFACT_DIR"/SharpLink.Hosting.*.nupkg)
if [[ ${#hosting_packages[@]} -ne 1 || ! -f "${hosting_packages[0]}" ]]; then
  echo "Expected exactly one SharpLink.Hosting nupkg in $ARTIFACT_DIR." >&2
  exit 1
fi

hosting_package="${hosting_packages[0]}"
version="${hosting_package#"$ARTIFACT_DIR/SharpLink.Hosting."}"
version="${version%.nupkg}"
if ! unzip -p "$hosting_package" SharpLink.Hosting.nuspec |
   grep -F "<dependency id=\"SharpLink.Runtime\" version=\"$version\"" >/dev/null; then
  echo "SharpLink.Hosting must directly depend on SharpLink.Runtime $version." >&2
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
NUGET_PACKAGES="$package_cache" dotnet run \
  --configuration Release \
  --no-restore \
  --project "$PROJECT" \
  -p:SharpLinkPackageVersion="$version"
