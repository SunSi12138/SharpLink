#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_directory="${1:-$repo_root/artifacts/nuget}"
package_version="${2:-2.0.0}"
dotnet build "$repo_root/eng/SharpLink.PublicApi/SharpLink.PublicApi.csproj" -c Release -v minimal
python3 "$repo_root/eng/verify-public-api.py" "$package_directory" --version "$package_version"
