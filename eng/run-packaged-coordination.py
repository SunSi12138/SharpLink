#!/usr/bin/env python3
"""Compile the public coordination smoke and its plugins with an isolated release package feed."""

import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
HOST = "SharpLink.VersionInventorySmoke"
CONTRACT = "SharpLink.DynamicPlugin.Contracts"
SERVICE = "SharpLink.DynamicPlugin.Services"

spec = importlib.util.spec_from_file_location("packaged_smoke", ROOT / "eng/run-packaged-trim-smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def stage_project(work, source_name, name, packages, *, host=False, project_reference=None):
    source = ROOT / "test" / source_name
    dest = work / name
    dest.mkdir()
    for file in sorted(source.glob("*.cs")):
        shutil.copyfile(file, dest / file.name)
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    props = ET.SubElement(project, "PropertyGroup")
    for key, value in {
        "TargetFramework": "net10.0", "AssemblyName": name, "ImplicitUsings": "enable",
        "Nullable": "enable", "TreatWarningsAsErrors": "true", "IsPackable": "false",
        "OutputType": "Exe" if host else "Library",
    }.items():
        ET.SubElement(props, key).text = value
    references = ET.SubElement(project, "ItemGroup")
    for package, version in packages.items():
        ET.SubElement(references, "PackageReference", Include=package, Version=version)
    if project_reference:
        ET.SubElement(references, "ProjectReference", Include=f"../{project_reference}/{project_reference}.csproj")
    output = dest / f"{name}.csproj"
    ET.indent(project)
    ET.ElementTree(project).write(output, encoding="utf-8")
    return output


def verify_assets(assets, cache, manifest, version, expected_project):
    references, packages = set(), {}
    for identity, library in assets["libraries"].items():
        name, resolved_version = identity.split("/", 1)
        if library["type"] == "project":
            references.add(name)
            continue
        if library["type"] != "package":
            raise ValueError(f"Unexpected dependency type: {identity}")
        if not name.startswith("SharpLink."):
            continue
        filename = f"{name}.{resolved_version}.nupkg"
        if resolved_version != version or filename not in manifest:
            raise ValueError(f"Out-of-closure product dependency: {identity}")
        expected = manifest[filename]
        resolved = cache / library["path"] / filename.lower()
        if library["sha512"] != expected["sha512"] or smoke.digest(resolved).hex() != expected["sha256"]:
            raise ValueError(f"Restored package differs from the release gate: {identity}")
        packages[name] = resolved_version
    expected_references = {expected_project} if expected_project else set()
    if references != expected_references or "SharpLink.Sdk" not in packages:
        raise ValueError(f"Unexpected project and package ownership: {references}, {packages}")
    return packages


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-directory", type=Path, required=True)
    parser.add_argument("--pack-revision", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    evidence_dir = args.output.resolve()
    evidence_dir.mkdir(parents=True)
    work = evidence_dir.parent / (evidence_dir.name + "-projects")
    work.mkdir()
    evidence = dict(status="failed", pack_revision=args.pack_revision, commands=[], graphs={}, cases=[])
    try:
        version = json.loads((ROOT / "eng/release-versions.json").read_text())["release"]
        evidence.update(version=version, rid=smoke.native_rid())
        feed = args.package_directory.resolve()
        manifest = smoke.package_manifest(feed, version, args.pack_revision)
        config = ET.parse(ROOT / "test/SharpLink.PackageSmoke/NuGet.config")
        config.find("./packageSources/add[@key='SharpLink package smoke feed']").set("value", str(feed))
        config.write(work / "NuGet.config", encoding="utf-8", xml_declaration=True)
        cache = work / "packages"
        env = os.environ | dict(NUGET_PACKAGES=str(cache), NUGET_HTTP_CACHE_PATH=str(work / "http"))

        def run(command, log):
            return smoke.run(command, evidence_dir / log, env, evidence)

        def released(*names):
            return {"SharpLink." + name: f"[{version}]" for name in names}

        props = ET.parse(ROOT / "Directory.Packages.props")
        sharp_pack = props.find(".//PackageVersion[@Include='SharpPack']").get("Version")
        projects = [
            stage_project(work, "SharpLink.DynamicContracts", CONTRACT,
                          released("Sdk", "Serializer.SharpPack") | {"SharpPack": sharp_pack}),
            stage_project(work, "SharpLink.DynamicServices", SERVICE, released("Sdk"), project_reference=CONTRACT),
            stage_project(work, HOST, HOST, released("Client", "Server", "Sdk", "Serializer.SharpPack"), host=True),
        ]
        run(["dotnet", "--info"], "sdk.log")
        for project in projects:
            name = project.stem
            run(["dotnet", "restore", project, "--force", "--no-cache", "--configfile", work / "NuGet.config"],
                name + ".restore.log")
            assets = json.loads((project.parent / "obj/project.assets.json").read_text())
            smoke.write_json(evidence_dir / (name + ".assets.json"), assets)
            evidence["graphs"][name] = verify_assets(assets, cache, manifest, version,
                                                       CONTRACT if name == SERVICE else None)
            run(["dotnet", "build", project, "-c", "Release", "--no-restore", "--disable-build-servers"], name + ".build.log")
        if not {"SharpLink.Client", "SharpLink.Server"} <= evidence["graphs"][HOST].keys():
            raise ValueError("The host did not restore both product endpoint packages")
        plugins = work / "deployment"
        plugins.mkdir()
        for name in (CONTRACT, SERVICE):
            shutil.copyfile(work / name / "bin/Release/net10.0" / (name + ".dll"), plugins / (name + ".dll"))
        host = work / HOST / "bin/Release/net10.0" / (HOST + ".dll")
        host_deps = json.loads(host.with_suffix(".deps.json").read_text())
        if {CONTRACT, SERVICE} & {key.split("/", 1)[0] for key in host_deps["libraries"]}:
            raise ValueError("The application statically references a dynamic plugin")

        cases = [
            ("tcp-manual", ("coordinate", "tcp", "manual"), (
                "VERSION_DEFERRAL_PASS transport=tcp distinct=true client=true server=true fresh-retry=true",
                "VERSION_COORDINATION_PASS transport=tcp mode=manual exact=true original-drained=true",
            )),
            ("shm-automatic", ("coordinate", "shared-memory", "automatic"), (
                "VERSION_COORDINATION_PASS transport=shared-memory mode=automatic exact=true original-drained=true",
            )),
            ("tcp-independent-endpoints", ("independent-endpoints", "tcp", "static"), (
                "VERSION_INDEPENDENT_ENDPOINTS_PASS transport=tcp topology=static isolated=true refreshed=true",
            )),
        ]
        for name, argv, markers in cases:
            output = run(["dotnet", host, *argv, plugins], name + ".log")
            if any(output.splitlines().count(marker) != 1 for marker in markers):
                raise ValueError(f"{name}: missing exact public API success marker")
            evidence["cases"].append(dict(name=name, status="passed", markers=markers))
        evidence["status"] = "passed"
    finally:
        smoke.write_json(evidence_dir / "evidence.json", evidence)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
