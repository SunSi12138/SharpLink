#!/usr/bin/env python3
"""Phase pins and unchanged original full-product preparation/identity helpers; no timing here."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

BASELINE = "ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c"
CANDIDATE = "2153cd9228c1de25e89a4bb68f0e3da4dc19f867"
CANDIDATE_ORIGINAL = "6077617167adbfd561e494d733fac2e1779f218f"
CANDIDATE_TREE = "02a2824f344c3614fff8de1d8a10ed7a5be87c21"
SDK = "10.0.102"
RUNTIME = "10.0.14"
PACKAGES = ("Abstractions", "Client", "Compression.Zstd", "GenerationControl", "Hosting",
            "Runtime", "Sdk", "Serializer.SharpPack", "Server")


def write(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def capture(command, cwd=None):
    return subprocess.check_output(command, cwd=cwd, text=True).strip()


def run(command, log, cwd=None, environment=None, check=True):
    print("RUN", " ".join(map(str, command)), flush=True)
    with log.open("w") as stream:
        subprocess.run(list(map(str, command)), cwd=cwd, env=environment,
                       stdout=stream, stderr=subprocess.STDOUT, check=check)


def verify_source(root, expected):
    actual = capture(["git", "rev-parse", "HEAD"], root)
    if actual != expected or capture(["git", "status", "--porcelain"], root):
        raise ValueError(f"Expected clean {expected} checkout: {root}; found {actual}")


def validate_runtime(dotnet):
    listing = capture([str(dotnet), "--list-runtimes"])
    expected = {("Microsoft.NETCore.App", RUNTIME), ("Microsoft.AspNetCore.App", RUNTIME)}
    actual = {tuple(line.split()[:2]) for line in listing.splitlines()}
    if actual != expected or capture([str(dotnet), "--list-sdks"]):
        raise ValueError(f"Expected runtime-only private .NETCore + AspNet {RUNTIME}; found {listing}")
    for line in listing.splitlines():
        location = Path(line.split("[", 1)[1].rstrip("]")).resolve()
        if not location.is_relative_to(dotnet.parent):
            raise ValueError(f"Runtime escaped private installation: {line}")
    return listing


def pin_snapshot(root):
    """Framework versions are exact; no roll-forward to any installed servicing patch."""
    configs = list(root.glob("*.runtimeconfig.json"))
    if len(configs) != 1:
        raise ValueError(f"Expected one runtimeconfig: {root}")
    data = json.loads(configs[0].read_text())
    options = data["runtimeOptions"]
    frameworks = options.get("frameworks", [options.get("framework")])
    if {f["name"] for f in frameworks if f} != {"Microsoft.NETCore.App", "Microsoft.AspNetCore.App"}:
        raise ValueError(f"Unexpected frameworks: {configs[0]}")
    for framework in frameworks:
        framework["version"] = RUNTIME
    options["rollForward"] = "Disable"
    write(configs[0], data)


def verify_dependencies(baseline, candidate, identical):
    b = {p.name: p for p in baseline.glob("*.dll")}
    c = {p.name: p for p in candidate.glob("*.dll")}
    if b.keys() != c.keys():
        raise ValueError("Dependency inventories differ")
    for name in b:
        if (not name.startswith("SharpLink.") or name in identical) and b[name].read_bytes() != c[name].read_bytes():
            raise ValueError(f"Non-product binary mismatch: {name}")
    return {variant: {name: hashlib.sha256(p.read_bytes()).hexdigest() for name, p in files.items()}
            for variant, files in (("baseline", b), ("candidate", c))}


def verify_process(data, private_root):
    if data["runtimeVersion"] != RUNTIME:
        raise ValueError(f"Wrong executing runtime: {data['runtimeVersion']}")
    if {Path(p).parent.parent.name for p in data["frameworkLocations"]} != {"Microsoft.NETCore.App", "Microsoft.AspNetCore.App"}:
        raise ValueError("Both executing framework locations are required")
    for path in data["frameworkLocations"]:
        if not Path(path).is_relative_to(private_root / "shared") or RUNTIME not in Path(path).parts:
            raise ValueError(f"Wrong executing framework path: {path}")

