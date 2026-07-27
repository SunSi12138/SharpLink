#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACT_DIR="${1:-$ROOT/artifacts/nuget}"
ARTIFACT_DIR="$(cd "$ARTIFACT_DIR" && pwd)"
EXPECTED_COMMIT="$(git -C "$ROOT" rev-parse HEAD)"
EXPECTED_VERSION="${2:-}"

if [[ "${SHARPLINK_ALLOW_DIRTY_PACKAGE_VERIFY:-false}" != "true" ]] &&
   [[ -n "$(git -C "$ROOT" status --short)" ]]; then
  echo "Package identity verification requires a clean worktree." >&2
  exit 1
fi

PACKAGES=(
  SharpLink.Abstractions
  SharpLink.Client
  SharpLink.Hosting
  SharpLink.Runtime
  SharpLink.Sdk
  SharpLink.Serializer.SharpPack
  SharpLink.Server
)

for package_id in "${PACKAGES[@]}"; do
  nupkg_candidates=("$ARTIFACT_DIR/$package_id."*.nupkg)
  snupkg_candidates=("$ARTIFACT_DIR/$package_id."*.snupkg)
  if [[ ${#nupkg_candidates[@]} -ne 1 || ! -f "${nupkg_candidates[0]}" ]]; then
    echo "Expected exactly one $package_id nupkg in $ARTIFACT_DIR." >&2
    exit 1
  fi
  if [[ ${#snupkg_candidates[@]} -ne 1 || ! -f "${snupkg_candidates[0]}" ]]; then
    echo "Expected exactly one $package_id snupkg in $ARTIFACT_DIR." >&2
    exit 1
  fi

  nupkg="${nupkg_candidates[0]}"
  snupkg="${snupkg_candidates[0]}"
  version="${nupkg#"$ARTIFACT_DIR/$package_id."}"
  version="${version%.nupkg}"
  if [[ -z "$EXPECTED_VERSION" ]]; then
    EXPECTED_VERSION="$version"
  elif [[ "$version" != "$EXPECTED_VERSION" ]]; then
    echo "$package_id has version $version; expected $EXPECTED_VERSION." >&2
    exit 1
  fi

  unzip -Z1 "$nupkg" | rg -Fxq "lib/net10.0/$package_id.dll"
  unzip -Z1 "$nupkg" | rg -Fxq "lib/net10.0/$package_id.xml"
  unzip -Z1 "$snupkg" | rg -Fxq "lib/net10.0/$package_id.pdb"
  unzip -p "$nupkg" "$package_id.nuspec" | rg -Fq "commit=\"$EXPECTED_COMMIT\""
done

unzip -Z1 "$ARTIFACT_DIR/SharpLink.Sdk.$EXPECTED_VERSION.nupkg" |
  rg -Fxq "analyzers/dotnet/cs/SharpLink.Generator.dll"

echo "Verified ${#PACKAGES[@]} package and symbol pairs for $EXPECTED_VERSION at $EXPECTED_COMMIT."
