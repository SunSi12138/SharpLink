#!/usr/bin/env python3
"""Rebuild a genuine old API5 plugin with its unmodified historical generator.

This is historical-source replay, NOT a retained artifact from the original CI.
No binary/manifest rewriting, Reflection.Emit, or copied generator outputs.
"""
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

OLD = "d5665d057e18f9b39fe80bcc5911900eb4663ce2"
OLD_ABI = "sharplink-3.0-api5-owner-bound-unsafe-blit-v1"
NEW_ABI = "sharplink-3.0-api6-contract-binding-v1"
ROOT = Path(__file__).resolve().parent
if len(sys.argv) != 5:
    raise SystemExit("usage: verify-real-api5-binary.py OLD_ROOT CURRENT_ROOT OUTPUT EXPECTED_CURRENT_COMMIT")
OLD_ROOT, NEW_ROOT, OUTPUT = [Path(p).resolve() for p in sys.argv[1:4]]
CURRENT = sys.argv[4]
OUTPUT.mkdir(parents=True, exist_ok=False)


def run(command, directory, log):
    result = subprocess.run(command, cwd=directory, encoding="utf-8", errors="replace", stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (OUTPUT / log).write_text(result.stdout, encoding="utf-8")
    if result.returncode:
        raise SystemExit(f"Failed {log}:\n{result.stdout[-6000:]}")
    return result.stdout



def project(path, packages, version, executable=False):
    root = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(root, "PropertyGroup")
    for name, value in {"TargetFramework": "net10.0", "ImplicitUsings": "enable", "Nullable": "enable",
                        "AllowUnsafeBlocks": "true", "TreatWarningsAsErrors": "true",
                        "ManagePackageVersionsCentrally": "false",
                        "EmitCompilerGeneratedFiles": "true",
                        "CompilerGeneratedFilesOutputPath": "obj/generated",
                        "OutputType": "Exe" if executable else "Library"}.items():
        ET.SubElement(properties, name).text = value
    items = ET.SubElement(root, "ItemGroup")
    for package in packages:
        ET.SubElement(items, "PackageReference", Include=package, Version=f"[{version}]")
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


metadata = {"kind": "historical-source replay, not retained original artifact", "sources": {}, "files": {}}
run(["dotnet", "--info"], NEW_ROOT, "dotnet-info.log")
for label, source, expected in [("old", OLD_ROOT, OLD), ("current", NEW_ROOT, CURRENT)]:
    actual = run(["git", "rev-parse", "HEAD"], source, f"{label}.commit.log").strip()
    if actual != expected or run(["git", "status", "--porcelain"], source, f"{label}.status.log").strip():
        raise SystemExit("Source checkout must match the exact clean pinned commit.")
    metadata["sources"][label] = actual
    version = ET.parse(source / "Directory.Build.props").findtext(".//VersionPrefix")
    if not version:
        raise SystemExit("Source must declare a package version.")
    feed = OUTPUT / label / "feed"
    feed.mkdir(parents=True)
    for package in ["SharpLink.Abstractions", "SharpLink.Runtime", "SharpLink.Client", "SharpLink.Sdk"]:
        run(["dotnet", "pack", f"src/{package}/{package}.csproj", "-c", "Release", "-o", str(feed), "-v", "minimal"],
            source, f"{label}.{package}.pack.log")
        run([sys.executable, "eng/verify-package-identities.py", str(feed / f"{package}.{version}.nupkg"),
             str(feed / f"{package}.{version}.snupkg"), package, version, expected],
            source, f"{label}.{package}.identity.log")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="candidate", value=str(feed))
    ET.SubElement(sources, "add", key="nuget", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    local = ET.SubElement(mapping, "packageSource", key="candidate")
    ET.SubElement(local, "package", pattern="SharpLink.*")
    public = ET.SubElement(mapping, "packageSource", key="nuget")
    ET.SubElement(public, "package", pattern="*")
    config_path = OUTPUT / label / "NuGet.config"
    ET.ElementTree(config).write(config_path, encoding="utf-8", xml_declaration=True)
    for name, packages, is_host in [("Plugin", ["SharpLink.Sdk"], False),
                                   ("Host", ["SharpLink.Client", "SharpLink.Sdk"], True)]:
        directory = OUTPUT / label / name
        directory.mkdir()
        csproj = directory / f"{name}.csproj"
        project(csproj, packages, version, is_host)
        content = (ROOT / "Host.cs").read_text(encoding="utf-8-sig") if is_host else (OLD_ROOT / "test/SharpLink.SdkOnlyPackageSmoke/EchoContract.cs").read_text(encoding="utf-8-sig")
        (directory / "Source.cs").write_text(content, encoding="utf-8")
        common = [str(csproj), "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false"]
        run(["dotnet", "restore", *common, "--configfile", str(config_path), "--packages", str(OUTPUT / label / "cache"), "--no-cache"],
            source, f"{label}.{name}.restore.log")
        run(["dotnet", "build", *common, "-c", "Release", "--no-restore", "-p:UseSharedCompilation=false", "-m:1"],
            source, f"{label}.{name}.build.log")
    for file in feed.glob("*.nupkg"):
        metadata["files"][str(file.relative_to(OUTPUT))] = {"bytes": file.stat().st_size}
    plugin = OUTPUT / label / "Plugin/bin/Release/net10.0/Plugin.dll"
    metadata["files"][str(plugin.relative_to(OUTPUT))] = {"bytes": plugin.stat().st_size}
    for name in ["Plugin", "Host"]:
        for relative in [f"{name}.csproj", "Source.cs", "obj/project.assets.json",
                         f"bin/Release/net10.0/{name}.dll"]:
            file = OUTPUT / label / name / relative
            metadata["files"][str(file.relative_to(OUTPUT))] = {"bytes": file.stat().st_size}
    metadata["files"][str(config_path.relative_to(OUTPUT))] = {"bytes": config_path.stat().st_size}

# Separate processes prevent old/current package identities or module initializers
# from contaminating each other. Positives run before the cross-generation case.
for name, host, plugin, identity, reject in [
    ("old-old", "old", "old", OLD_ABI, False),
    ("current-current", "current", "current", NEW_ABI, False),
    ("current-old", "current", "old", OLD_ABI, True),
]:
    host_dll = OUTPUT / host / "Host/bin/Release/net10.0/Host.dll"
    plugin_dll = OUTPUT / plugin / "Plugin/bin/Release/net10.0/Plugin.dll"
    run(["dotnet", str(host_dll), str(plugin_dll), identity, str(reject), OLD_ABI if host == "old" else NEW_ABI, "5" if host == "old" else "6", "5" if plugin == "old" else "6"], NEW_ROOT, f"{name}.run.log")
(OUTPUT / "result.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
print("Real generated API5 binary replay passed: API5/API5 and API6/API6 register; API6/API5 rejects exact ABI.")
