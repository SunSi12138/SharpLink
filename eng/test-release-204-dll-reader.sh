#!/usr/bin/env bash
set -euo pipefail
tooling="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/src" "$tmp/test/SharpLink.Probe"
cat > "$tmp/test/SharpLink.Probe/SharpLink.Probe.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><GenerateAssemblyInfo>false</GenerateAssemblyInfo></PropertyGroup></Project>
XML
cat > "$tmp/test/SharpLink.Probe/Probe.cs" <<'CS'
[assembly:System.Reflection.AssemblyVersion("2.0.4.0")]
[assembly:System.Reflection.AssemblyInformationalVersion("2.0.4+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
public class Probe { }
CS
printf '<configuration><packageSources><clear /></packageSources></configuration>\n' > "$tmp/NuGet.Config"
dotnet build "$tmp/test/SharpLink.Probe/SharpLink.Probe.csproj" -c Release --configfile "$tmp/NuGet.Config" -v minimal
bash "$tooling/run-release-204-dll-reader.sh" "$tmp" aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa "$tmp/identity.json"
python3 - "$tmp" <<'PY'
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[1])
items = json.loads((root / 'identity.json').read_text())['Assemblies']
assert len(items) == 1, items
item = items[0]
assert item['Name'] == 'SharpLink.Probe' and item['AssemblyVersion'] == '2.0.4.0', item
assert item['InformationalVersion'] == '2.0.4+' + 'a' * 40, item
assert item['Sha256'] == hashlib.sha256((root / item['Path']).read_bytes()).hexdigest(), item
PY
# A damaged/non-assembly DLL must not produce apparently usable evidence.
printf 'not a managed assembly' > "$tmp/test/SharpLink.Probe/bin/Release/net10.0/SharpLink.Probe.dll"
if bash "$tooling/run-release-204-dll-reader.sh" "$tmp" aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa "$tmp/invalid.json"; then
  echo 'DLL reader incorrectly accepted an invalid assembly.' >&2
  exit 1
fi
test ! -e "$tmp/invalid.json"
