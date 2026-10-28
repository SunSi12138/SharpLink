#!/usr/bin/env python3
"""Run the existing PackageReference full/partial Trim and AOT reject controls natively.

Each consumer gets a new global/HTTP cache and MSBuild artifacts directory. Evidence
is retained on failure; this is not a ProjectReference or cross-compilation gate.
"""

import argparse
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[1]
PACKAGES = tuple("SharpLink." + name for name in (
    "Abstractions", "Client", "Compression.Zstd", "GenerationControl", "Hosting",
    "Runtime", "Sdk", "Serializer.SharpPack", "Server"))
CONSUMERS = (("PackageTrimSmoke", "full", False),
             ("PackagePartialTrimSmoke", "partial", False),
             ("PackageAotSmoke", "full", True))
WARNINGS = ["-p:TreatWarningsAsErrors=true", "-p:ILLinkTreatWarningsAsErrors=true",
            "-p:IlcTreatWarningsAsErrors=true", "-p:TrimmerSingleWarn=false"]

_spec = importlib.util.spec_from_file_location("package_identities", ROOT / "eng/verify-package-identities.py")
_identities = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_identities)


def digest(path, algorithm="sha256"):
    with Path(path).open("rb") as stream:
        result = hashlib.new(algorithm)
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
        return result.digest()


def native_rid(system=None, machine=None):
    system = system or platform.system()
    machine = (machine or platform.machine()).lower()
    hosts = {("Linux", "x86_64"): "linux-x64", ("Windows", "amd64"): "win-x64",
             ("Windows", "x86_64"): "win-x64", ("Darwin", "arm64"): "osx-arm64"}
    if (system, machine) not in hosts:
        raise ValueError(f"Unsupported native packaged smoke host: {system}/{machine}")
    return hosts[system, machine]


def package_manifest(directory, version, commit):
    expected = {f"{name}.{version}.{ext}" for name in PACKAGES for ext in ("nupkg", "snupkg")}
    actual = {p.name for p in directory.iterdir() if p.suffix in (".nupkg", ".snupkg")}
    if actual != expected:
        raise ValueError(f"Nine-package closure mismatch: missing={sorted(expected - actual)}, extra={sorted(actual - expected)}")
    result = {}
    for name in PACKAGES:
        nupkg, snupkg = (directory / f"{name}.{version}.{ext}" for ext in ("nupkg", "snupkg"))
        _identities.verify_pair(nupkg, snupkg, name, version, commit)
        with ZipFile(nupkg) as archive:
            root = ET.fromstring(archive.read(f"{name}.nuspec"))
        for dependency in root.findall("./{*}metadata/{*}dependencies//{*}dependency"):
            dep_id = dependency.get("id", "")
            if dep_id.startswith("SharpLink.") and (
                    dep_id not in PACKAGES or dependency.get("version") not in (version, f"[{version}]")):
                raise ValueError(f"{name}: dependency outside candidate closure: {dependency.attrib}")
        for path in (nupkg, snupkg):
            result[path.name] = {"sha256": digest(path).hex(),
                                 "sha512": base64.b64encode(digest(path, "sha512")).decode()}
    return result


def verify_properties(evaluation, mode, aot):
    expected = {"PublishTrimmed": "true", "PublishAot": str(aot).lower(), "TrimMode": mode,
                "TreatWarningsAsErrors": "true", "ILLinkTreatWarningsAsErrors": "true",
                "IlcTreatWarningsAsErrors": "true"}
    for key, value in expected.items():
        if evaluation["Properties"].get(key, "").lower() != value:
            raise ValueError(f"Consumer property {key} must be {value}: {evaluation['Properties']}")
    if evaluation.get("Items", {}).get("ProjectReference"):
        raise ValueError("Packaged consumer must not have ProjectReference items")


def verify_assets(assets, cache, manifest, version):
    consumed = {}
    for identity, library in assets["libraries"].items():
        if library["type"] != "package":
            raise ValueError(f"Non-package consumer dependency: {identity}")
        name, resolved_version = identity.split("/")
        if not name.startswith("SharpLink."):
            continue
        filename = f"{name}.{resolved_version}.nupkg"
        if resolved_version != version or filename not in manifest:
            raise ValueError(f"Consumer resolved a non-candidate package: {identity}")
        expected = manifest[filename]
        package = cache / library["path"] / f"{name.lower()}.{resolved_version.lower()}.nupkg"
        if library.get("sha512") != expected["sha512"] or digest(package).hex() != expected["sha256"]:
            raise ValueError(f"Restored package bytes differ from candidate: {identity}")
        consumed[identity] = expected["sha256"]
    required = {f"SharpLink.{name}/{version}" for name in ("Abstractions", "GenerationControl", "Runtime", "Sdk")}
    if not required.issubset(consumed):
        raise ValueError(f"Missing required candidate dependencies: {sorted(required - consumed.keys())}")
    return consumed


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def run(command, log, environment, evidence):
    command = [str(arg) for arg in command]
    print("+ " + subprocess.list2cmdline(command), flush=True)
    result = subprocess.run(command, cwd=ROOT, env=environment, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
    log.write_text(result.stdout, encoding="utf-8")
    print(result.stdout, end="", flush=True)
    evidence["commands"].append({"argv": command, "exitCode": result.returncode, "log": log.name})
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}); see {log}")
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=("linux-x64", "win-x64", "osx-arm64"))
    parser.add_argument("--package-directory", type=Path, default=ROOT / "artifacts/nuget")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/packaged-trim")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    output = Path(tempfile.mkdtemp(prefix=args.rid + "-", dir=args.output.resolve()))
    evidence = {"status": "failed", "rid": args.rid, "host": platform.platform(), "commands": [], "consumers": []}
    try:
        if native_rid() != args.rid:
            raise ValueError(f"Refusing cross-compiled execution: host={native_rid()}, requested={args.rid}")
        commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        if subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip():
            raise ValueError("Packaged smoke requires a clean source checkout")
        version = json.loads((ROOT / "eng/release-versions.json").read_text())["release"]
        evidence.update(commit=commit, version=version)
        manifest = package_manifest(args.package_directory.resolve(), version, commit)
        evidence["packages"] = manifest
        env = os.environ.copy()
        dotnet = os.environ.get("DOTNET", "dotnet")
        run([dotnet, "--info"], output / "dotnet-info.log", env, evidence)
        # Work is separate from uploaded evidence: never upload package caches or stale binaries.
        work = Path(tempfile.mkdtemp(prefix="packaged-consumers-", dir=ROOT / "artifacts"))
        evidence["workDirectory"] = str(work)
        # Reuse the checked-in source mapping but bind its local feed to this candidate.
        config = ET.parse(ROOT / "test/SharpLink.PackageSmoke/NuGet.config")
        for source in config.findall("./packageSources/add"):
            if source.get("key") == "SharpLink package smoke feed":
                source.set("value", str(args.package_directory.resolve()))
        config.write(work / "NuGet.config", encoding="utf-8", xml_declaration=True)
        for consumer, mode, aot in CONSUMERS:
            name = "SharpLink." + consumer
            project = ROOT / "test" / name / (name + ".csproj")
            consumer_work = work / consumer
            cache = consumer_work / "packages"
            cache.mkdir(parents=True)  # Newly created, not a reusable NuGet cache.
            env.update(NUGET_PACKAGES=str(cache), NUGET_HTTP_CACHE_PATH=str(consumer_work / "http-cache"))
            artifacts = consumer_work / "build"
            common = ["--artifacts-path", artifacts, "-r", args.rid, "-p:SelfContained=true",
                      "-p:Configuration=Release", f"-p:SharpLinkPackageVersion={version}", *WARNINGS]
            run([dotnet, "restore", project, "--force", "--no-cache", "--configfile", work / "NuGet.config", *common],
                output / f"{consumer}-restore.log", env, evidence)
            evaluation = run([dotnet, "msbuild", project, "-nologo", f"-p:ArtifactsPath={artifacts}",
                              "-p:UseArtifactsOutput=true", "-p:Configuration=Release", f"-p:RuntimeIdentifier={args.rid}",
                              "-p:SelfContained=true", f"-p:SharpLinkPackageVersion={version}", *WARNINGS,
                              "-getProperty:PublishTrimmed,PublishAot,TrimMode,TreatWarningsAsErrors,ILLinkTreatWarningsAsErrors,IlcTreatWarningsAsErrors",
                              "-getItem:ProjectReference"], output / f"{consumer}-properties.json", env, evidence)
            verify_properties(json.loads(evaluation), mode, aot)
            assets_paths = list(artifacts.rglob("project.assets.json"))
            if len(assets_paths) != 1:
                raise ValueError(f"Expected one isolated consumer assets file, got {assets_paths}")
            assets = json.loads(assets_paths[0].read_text(encoding="utf-8"))
            write_json(output / f"{consumer}-assets.json", assets)
            consumed = verify_assets(assets, cache, manifest, version)
            publish = consumer_work / "publish"
            run([dotnet, "publish", project, "--no-restore", "-c", "Release", "--self-contained", "true",
                 "-o", publish, *common], output / f"{consumer}-publish.log", env, evidence)
            executable = publish / (name + (".exe" if args.rid == "win-x64" else ""))
            if not executable.is_file() or (publish / (name + ".dll")).exists() == aot:
                raise ValueError(f"Expected {'NativeAOT' if aot else 'trimmed managed'} publish layout: {publish}")
            # Existing Main performs both round-trip and fail-closed missing-ABI reject controls.
            result = run([executable], output / f"{consumer}-run.log", env, evidence)
            if not aot and "PACKAGE_TRIM_SMOKE_PASS" not in result.splitlines():
                raise ValueError(f"{consumer} did not emit its success marker")
            evidence["consumers"].append({"name": name, "trimMode": mode, "publishAot": aot,
                                           "packages": consumed, "executableSha256": digest(executable).hex(), "status": "passed"})
        if package_manifest(args.package_directory.resolve(), version, commit) != manifest:
            raise ValueError("Candidate packages changed during smoke")
        evidence["status"] = "passed"
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as exc:
        evidence["error"] = str(exc)
        print(f"Packaged trim/AOT smoke FAILED: {exc}", file=sys.stderr)
    finally:
        write_json(output / "evidence.json", evidence)
        print(f"Packaged trim/AOT evidence: {output}", flush=True)
    return 0 if evidence["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
