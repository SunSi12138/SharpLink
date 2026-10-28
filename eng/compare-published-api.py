#!/usr/bin/env python3
"""Record actual NuGet 2.0.3 -> candidate package APIs; differences are review evidence, not failures."""

import argparse
from datetime import datetime, timezone
import difflib
import hashlib
import io
import json
from pathlib import Path
import re
import runpy
import subprocess
import tempfile
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent.parent
PACKAGES = runpy.run_path(str(ROOT / "eng/verify-public-api.py"))["PACKAGES"]
# This is the released 2.0.3 inventory, not the evolving candidate package list.
PUBLISHED_PACKAGES = (
    "SharpLink.Abstractions", "SharpLink.Runtime", "SharpLink.Client", "SharpLink.Server",
    "SharpLink.Hosting", "SharpLink.Sdk", "SharpLink.Serializer.SharpPack", "SharpLink.Compression.Zstd",
)
PUBLISHED_VERSION = "2.0.3"
NUGET_SOURCE = "https://api.nuget.org/v3-flatcontainer"


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def extract_package(path, package, version, assemblies, source, expected_commit=None):
    """Validate identities and an unambiguous shipping DLL before extracting any bytes."""
    data = path.read_bytes()  # Missing input must fail, never reuse an old inventory.
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        names = archive.namelist()
        nuspecs = [name for name in names if name.lower().endswith(".nuspec")]
        if nuspecs != [f"{package}.nuspec"]:
            raise ValueError(f"{path}: expected exactly one root {package}.nuspec")
        metadata = ET.fromstring(archive.read(nuspecs[0])).find("./{*}metadata")
        if metadata is None:
            raise ValueError(f"{path}: missing nuspec metadata")
        actual_id = metadata.findtext("./{*}id")
        actual_version = metadata.findtext("./{*}version")
        if (actual_id, actual_version) != (package, version):
            raise ValueError(f"{path}: nuspec identity {actual_id}/{actual_version} != {package}/{version}")
        repository = metadata.find("./{*}repository")
        commit = repository.get("commit") if repository is not None else None
        if expected_commit is not None and commit != expected_commit:
            raise ValueError(f"{path}: repository commit {commit} != {expected_commit}")
        expected = f"lib/net10.0/{package}.dll"
        # Reject duplicates, case variants, alternate TFM/ref copies and unexpected
        # shipping DLLs. The SDK's separate analyzer DLL is deliberately out of scope.
        shipping_dlls = [name for name in names
                         if name.lower().startswith(("lib/", "ref/", "runtimes/"))
                         and name.lower().endswith(".dll")]
        if shipping_dlls != [expected]:
            raise ValueError(f"{path}: expected exactly one shipping DLL {expected}; found {shipping_dlls}")
        dll = archive.read(expected)
    (assemblies / f"{package}.dll").write_bytes(dll)
    return {
        "id": package, "version": version, "source": source,
        "package_sha256": sha256(data), "repository_commit": commit,
        "assembly_path": expected, "assembly_sha256": sha256(dll),
    }


def generate_inventory(assemblies, output, records, version, tool, expected_commit=None, packages=PACKAGES):
    subprocess.run(["dotnet", str(tool), str(assemblies), str(output)], check=True)
    expected_names = {f"{package}.{suffix}" for package in packages
                      for suffix in ("api.txt", "forwarders.txt", "assembly.json")}
    actual_names = {path.name for path in output.iterdir() if path.is_file()}
    if actual_names != expected_names:
        raise ValueError(f"Generated inventory mismatch: {actual_names ^ expected_names}")
    assembly_version = version.split("-", 1)[0] + ".0"
    for record in records:
        package = record["id"]
        identity = json.loads((output / f"{package}.assembly.json").read_text(encoding="utf-8"))
        if identity.get("Name") != package or identity.get("Version") != assembly_version:
            raise ValueError(f"{package}: unexpected assembly identity: {identity}")
        if expected_commit is not None and identity.get("InformationalVersion") != f"{version}+{expected_commit}":
            raise ValueError(f"{package}: assembly build identity does not match candidate version/commit")
        record["assembly_identity"] = identity
        record["inventory_sha256"] = {
            suffix: sha256((output / f"{package}.{suffix}").read_bytes())
            for suffix in ("api.txt", "forwarders.txt")
        }


def write_differences(output, current_version):
    differences = output / "diffs"
    differences.mkdir()
    lines = ["# Published 2.0.3 → candidate " + current_version + " API evidence", "",
             "These are textual public/protected API and ExportedType forwarding differences,",
             "not a semantic compatibility verdict or approval of breaking changes.",
             "The reviewed 3.0 baseline is neither read nor modified.", "",
             "| Assembly | Package status | API removed / added lines | Forwarders removed / added lines |",
             "| --- | --- | ---: | ---: |"]
    for package in sorted(set(PUBLISHED_PACKAGES) | set(PACKAGES)):
        added_package = package not in PUBLISHED_PACKAGES
        removed_package = package not in PACKAGES
        status = "Added package (no 2.0.3 release)" if added_package else "Removed package" if removed_package else "Existing package"
        counts = []
        for suffix in ("api.txt", "forwarders.txt"):
            name = f"{package}.{suffix}"
            old = "" if added_package else (output / "published" / name).read_text(encoding="utf-8")
            new = "" if removed_package else (output / "candidate" / name).read_text(encoding="utf-8")
            diff = list(difflib.unified_diff(old.splitlines(keepends=True), new.splitlines(keepends=True),
                                            fromfile="/dev/null" if added_package else f"published/{name}",
                                            tofile="/dev/null" if removed_package else f"candidate/{name}"))
            (differences / f"{name}.diff").write_text("".join(diff), encoding="utf-8")
            removed = sum(line.startswith("-") and not line.startswith("---") for line in diff)
            added = sum(line.startswith("+") and not line.startswith("+++") for line in diff)
            counts.append(f"[{removed} / {added}](diffs/{name}.diff)")
        lines.append(f"| {package} | {status} | {' | '.join(counts)} |")
    lines.extend(["", "Package sources, SHA-256 hashes, repository commits, assembly identities/MVIDs,",
                  "and inventory hashes are recorded in [provenance.json](provenance.json).",
                  "Each release's actual inventories are retained beside this report; no historical",
                  "inventory is fabricated for an added package. Its complete API and forwarders",
                  "are retained in candidate/ and represented as additions from /dev/null.", "",
                  f"Scope: {len(PUBLISHED_PACKAGES)} published and {len(PACKAGES)} candidate net10.0 shipping assemblies.",
                  "The SDK's analyzer implementation,",
                  "generated consumer compatibility, behavioral/wire compatibility, symbol classification,",
                  "and human approval of each breaking change remain separate release checks.", ""])
    (output / "README.md").write_text("\n".join(lines), encoding="utf-8")


def compare(packages, output, current_version, current_commit, tool):
    if not re.fullmatch(r"3\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", current_version):
        raise ValueError("Expected a candidate 3.x NuGet version")
    if not re.fullmatch(r"[0-9a-f]{40}", current_commit):
        raise ValueError("Expected a full candidate source commit SHA")
    if output.exists():
        raise ValueError(f"Output already exists; choose a fresh evidence directory: {output}")
    if not tool.is_file():
        raise ValueError(f"Build eng/SharpLink.PublicApi in Release first: {tool}")
    output.parent.mkdir(parents=True, exist_ok=True)
    # Publish the report only after every download, validation and extraction succeeds.
    with tempfile.TemporaryDirectory(prefix="sharplink-published-api-", dir=output.parent) as temporary:
        staging = Path(temporary)
        report = staging / "report"
        report.mkdir()
        provenance = {"schema_version": 1, "published_version": PUBLISHED_VERSION,
                      "candidate_version": current_version, "candidate_source_commit": current_commit,
                      "generator": "eng/SharpLink.PublicApi (PublicApiGenerator)",
                      "generator_sha256": sha256(tool.read_bytes()),
                      "package_sets": {"published": list(PUBLISHED_PACKAGES), "candidate": list(PACKAGES),
                                       "added": sorted(set(PACKAGES) - set(PUBLISHED_PACKAGES)),
                                       "removed": sorted(set(PUBLISHED_PACKAGES) - set(PACKAGES))},
                      "packages": {}}
        for label, version in (("candidate", current_version), ("published", PUBLISHED_VERSION)):
            assemblies = staging / f"{label}-assemblies"
            assemblies.mkdir()
            records = []
            package_set = PUBLISHED_PACKAGES if label == "published" else PACKAGES
            for package in package_set:
                if label == "published":
                    filename = f"{package.lower()}.{version}.nupkg"
                    source = f"{NUGET_SOURCE}/{package.lower()}/{version}/{filename}"
                    path = staging / filename
                    with urllib.request.urlopen(source, timeout=60) as response:
                        if response.url != source:
                            raise ValueError(f"Unexpected NuGet redirect: {response.url}")
                        path.write_bytes(response.read())
                else:
                    path = (packages / f"{package}.{version}.nupkg").resolve()
                    source = path.as_uri()
                record = extract_package(path, package, version, assemblies, source,
                                         current_commit if label == "candidate" else None)
                records.append(record)
            generate_inventory(assemblies, report / label, records, version, tool,
                               current_commit if label == "candidate" else None, packages=package_set)
            provenance["packages"][label] = records
        write_differences(report, current_version)
        provenance["completed_at_utc"] = datetime.now(timezone.utc).isoformat()
        (report / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
        report.rename(output)
    print(f"Recorded {len(PUBLISHED_PACKAGES)} published and {len(PACKAGES)} candidate package API/forwarder inventories in {output}. Differences require human review.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("packages", type=Path, help="Directory containing every candidate nupkg from the reviewed package inventory")
    parser.add_argument("--version", default=ET.parse(ROOT / "Directory.Build.props").findtext(".//VersionPrefix"))
    parser.add_argument("--current-commit", required=True, help="Full source SHA embedded in candidate nuspecs")
    parser.add_argument("--output", type=Path, required=True, help="New report directory (must not exist)")
    options = parser.parse_args()
    tool = ROOT / "eng/SharpLink.PublicApi/bin/Release/net10.0/SharpLink.PublicApi.dll"
    compare(options.packages, options.output.resolve(), options.version, options.current_commit, tool)


if __name__ == "__main__":
    main()
