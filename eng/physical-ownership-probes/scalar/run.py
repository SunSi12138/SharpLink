#!/usr/bin/env python3
"""Compare scalar intercepted-stream allocation, layout, IL, and JIT tiers.

This is an isolated diagnostic, not a throughput benchmark. Production DLLs must
already be built from the revisions supplied below. No production project is built.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(command, output, environment=None):
    with output.open("w") as stream:
        subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT,
                       env=environment, check=True)


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--baseline-libraries", type=Path, required=True)
parser.add_argument("--candidate-libraries", type=Path, required=True)
parser.add_argument("--baseline-revision", required=True)
parser.add_argument("--candidate-revision", required=True)
parser.add_argument("--output", type=Path, required=True)
parser.add_argument("--dotnet", default="dotnet")
parser.add_argument("--allocation-repeats", type=int, default=5)
args = parser.parse_args()
if args.allocation_repeats < 1:
    parser.error("--allocation-repeats must be positive")

source = Path(__file__).resolve().parent
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=False)
libraries = {"baseline": args.baseline_libraries.resolve(),
             "candidate": args.candidate_libraries.resolve()}
for root in libraries.values():
    for name in ("SharpLink.Client.dll", "SharpLink.Runtime.dll", "SharpLink.Abstractions.dll"):
        if not (root / name).is_file():
            parser.error(f"Missing prebuilt library: {root / name}")

# Build the same probe once, against the pinned baseline's existing API.
built = output / "built"
run([args.dotnet, "build", str(source / "probe.csproj"), "-c", "Release",
     "-m:1", "-nr:false", "-p:UseSharedCompilation=false",
     f"-p:LibraryRoot={libraries['baseline']}",
     f"-p:BaseIntermediateOutputPath={output / 'obj'}/", "-o", str(built)],
    output / "build.log")
run([args.dotnet, "--info"], output / "dotnet-info.txt")

environment = dict(os.environ)
for key in list(environment):
    if key.startswith(("DOTNET_Tiered", "COMPlus_Tiered", "DOTNET_Jit", "COMPlus_Jit",
                       "DOTNET_TC_", "COMPlus_TC_", "DOTNET_ReadyToRun", "COMPlus_ReadyToRun",
                       "DOTNET_ZapDisable", "COMPlus_ZapDisable")):
        del environment[key]

manifest = {"baselineRevision": args.baseline_revision,
            "candidateRevision": args.candidate_revision,
            "sourceSha256": {name: sha256(source / name)
                             for name in ("Program.cs", "probe.csproj", "run.py")},
            "environment": {key: value for key, value in environment.items()
                            if key.startswith(("DOTNET_", "COMPlus_"))},
            "variants": {}}
native = []
for variant, library in libraries.items():
    root = output / variant
    app = root / "app"
    shutil.copytree(built, app)
    # Keep the executable identical. Replace only dependencies already in its
    # output; adding unrelated assemblies would change manifest discovery.
    for target in app.glob("*.dll"):
        replacement = library / target.name
        if target.name != "SharpLink.Benchmarks.dll" and replacement.is_file():
            shutil.copy2(replacement, target)
    manifest["variants"][variant] = {path.name: sha256(path) for path in app.glob("*.dll")}
    for mode in ("fullopts", "default-tiered", "tiered-pgo-off"):
        directory = root / mode
        directory.mkdir()
        configured = dict(environment)
        if mode == "fullopts":
            configured["DOTNET_TieredCompilation"] = "0"
        elif mode == "tiered-pgo-off":
            configured["DOTNET_TieredPGO"] = "0"
        repeats = args.allocation_repeats if mode == "fullopts" else 1
        for repetition in range(1, repeats + 1):
            current = dict(configured)
            if repetition == 1:
                current["DOTNET_JitDisasm"] = "*InterceptedAsyncEnumerable*:* *SharpLinkModuleRpcChannel*:*"
                current["DOTNET_JitStdOutFile"] = str(directory / "jit.txt")
            run([args.dotnet, str(app / "SharpLink.Benchmarks.dll"), str(directory)],
                directory / f"run-{repetition}.log", current)
        listing = (directory / "jit.txt").read_text()
        for match in re.finditer(r"; Assembly listing for method (.*?) \(([^\n]+)\)\n(.*?); Total bytes of code (\d+)",
                                 listing, re.DOTALL):
            native.append({"variant": variant, "configuration": mode,
                           "method": match[1], "tier": match[2], "bytes": int(match[4])})

(output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
(output / "native.json").write_text(json.dumps(native, indent=2) + "\n")
print(f"Completed diagnostic comparison: {output}")
