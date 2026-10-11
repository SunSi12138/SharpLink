#!/usr/bin/env bash
set -euo pipefail
tooling="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(realpath "$1")"
sha="$2"
out="$(realpath -m "$3")"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cp "$tooling/inspect-release-204-dlls.cs" "$tmp/Program.cs"
cat > "$tmp/Inspector.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings><Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
XML
printf '<configuration><packageSources><clear /></packageSources></configuration>\n' > "$tmp/NuGet.Config"
dotnet build "$tmp/Inspector.csproj" -c Release --configfile "$tmp/NuGet.Config" -v minimal >&2
dotnet "$tmp/bin/Release/net10.0/Inspector.dll" "$root" "$sha" "$out"
