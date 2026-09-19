#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_directory="${1:-$repo_root/artifacts/nuget}"
package_version="${2:-$(python3 -c 'import sys, xml.etree.ElementTree as ET; print(ET.parse(sys.argv[1]).findtext(".//VersionPrefix"))' "$repo_root/Directory.Build.props")}"
dotnet build "$repo_root/eng/SharpLink.PublicApi/SharpLink.PublicApi.csproj" -c Release -v minimal
python3 "$repo_root/eng/verify-public-api.py" "$package_directory" --version "$package_version"
