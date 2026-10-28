#!/usr/bin/env python3
"""Prepare once, then compare scalar allocations/layout/JIT without rebuilding."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import statistics
import subprocess


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(command, output, environment=None):
    with output.open("w") as stream:
        subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT,
                       env=environment, check=True)


def prepare(args, output):
    source = Path(__file__).resolve().parent
    libraries = {"baseline": args.baseline_libraries.resolve(strict=True),
                 "candidate": args.candidate_libraries.resolve(strict=True)}
    build = output / "build"
    build.mkdir()
    for name in ("Program.cs", "probe.csproj"):
        shutil.copy2(source / name, build / name)
    for name in ("Directory.Build.props", "Directory.Build.targets"):
        (build / name).write_text("<Project />\n")
    built = output / "built"
    command = [args.dotnet, "build", str(build / "probe.csproj"), "-c", "Release",
               "-m:1", "-nr:false", "-p:UseSharedCompilation=false",
               f"-p:LibraryRoot={libraries['baseline']}", "-o", str(built), f"-bl:{output / 'build.binlog'}"]
    run(command, output / "build.log")
    run([args.dotnet, "--info"], output / "build-dotnet-info.txt")
    manifest = {"baselineRevision": args.baseline_revision,
                "candidateRevision": args.candidate_revision,
                "buildCommand": command,
                "sourceSha256": {name: sha256(source / name)
                                 for name in ("Program.cs", "probe.csproj", "run.py")},
                "variants": {}}
    for variant, library in libraries.items():
        app = output / variant / "app"
        shutil.copytree(built, app)
        for target in app.glob("*.dll"):
            if target.name == "SharpLink.Benchmarks.dll":
                continue
            replacement = library / target.name
            if not replacement.is_file():
                raise FileNotFoundError(f"Missing {variant} dependency: {replacement}")
            shutil.copy2(replacement, target)
        manifest["variants"][variant] = {p.name: sha256(p) for p in app.glob("*.dll")}
    baseline, candidate = manifest["variants"].values()
    for name in baseline:
        if (not name.startswith("SharpLink.") or name == "SharpLink.Benchmarks.dll") and baseline[name] != candidate[name]:
            raise ValueError(f"Non-product binary mismatch: {name}")
    (output / "prepared-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")


def measure(args, prepared, output):
    manifest = json.loads((prepared / "prepared-manifest.json").read_text())
    environment = dict(os.environ)
    for key in list(environment):
        if key.startswith(("DOTNET_Tiered", "COMPlus_Tiered", "DOTNET_Jit", "COMPlus_Jit",
                           "DOTNET_TC_", "COMPlus_TC_", "DOTNET_ReadyToRun", "COMPlus_ReadyToRun",
                           "DOTNET_ZapDisable", "COMPlus_ZapDisable")):
            del environment[key]
    manifest["environment"] = {key: value for key, value in environment.items()
                               if key.startswith(("DOTNET_", "COMPlus_"))}
    manifest["runtimeDotnet"] = args.runtime_dotnet or args.dotnet
    manifest["affinity"] = args.affinity
    manifest["allocationRepeats"] = args.allocation_repeats
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    native, allocations = [], []
    for mode in ("fullopts", "default-tiered", "tiered-pgo-off"):
        configured = dict(environment)
        if mode == "fullopts":
            configured["DOTNET_TieredCompilation"] = "0"
        elif mode == "tiered-pgo-off":
            configured["DOTNET_TieredPGO"] = "0"
        repeats = args.allocation_repeats if mode == "fullopts" else 1
        for repetition in range(1, repeats + 1):
            order = ("baseline", "candidate") if repetition % 2 else ("candidate", "baseline")
            for variant in order:
                directory = output / variant / mode / f"run-{repetition}"
                directory.mkdir(parents=True)
                current = dict(configured)
                if repetition == 1:
                    current["DOTNET_JitDisasm"] = "*InterceptedAsyncEnumerable*:* *SharpLinkModuleRpcChannel*:*"
                    current["DOTNET_JitStdOutFile"] = str(directory / "jit.txt")
                command = [] if args.affinity == "none" else ["taskset", "-c", args.affinity]
                command += [args.runtime_dotnet or args.dotnet,
                            str(prepared / variant / "app" / "SharpLink.Benchmarks.dll"), str(directory)]
                run(command, directory / "run.log", current)
                for row in json.loads((directory / "allocations.json").read_text()):
                    allocations.append(dict(row, variant=variant, configuration=mode, repetition=repetition))
                if repetition == 1:
                    listing = (directory / "jit.txt").read_text()
                    matches = list(re.finditer(r"; Assembly listing for method (.*?) \(([^\n]+)\)\n(.*?); Total bytes of code (\d+)", listing, re.DOTALL))
                    if not matches:
                        raise ValueError(f"No native listings captured: {directory}")
                    for match in matches:
                        native.append({"variant": variant, "configuration": mode,
                                       "method": match[1], "tier": match[2], "bytes": int(match[4])})
    summary = []
    for dynamic in (False, True):
        for timed in (False, True):
            for items in (0, 1, 100):
                rows = [r for r in allocations if r["configuration"] == "fullopts" and
                        (r["dynamic"], r["timed"], r["items"]) == (dynamic, timed, items)]
                values = {v: [r["bytesPerCall"] for r in rows if r["variant"] == v]
                          for v in ("baseline", "candidate")}
                b, c = values.values()
                summary.append({"dynamic": dynamic, "timed": timed, "items": items,
                                "baselineSamples": b, "candidateSamples": c,
                                "baselineMedian": statistics.median(b), "candidateMedian": statistics.median(c),
                                "baselineRange": [min(b), max(b)], "candidateRange": [min(c), max(c)],
                                "ratioOfMedians": statistics.median(c) / statistics.median(b) if statistics.median(b) else None,
                                "pairedRatios": [y / x if x else None for x, y in zip(b, c)],
                                "pairedDifferences": [y - x for x, y in zip(b, c)]})
    for name, value in (("native", native), ("allocations", allocations), ("summary", summary)):
        (output / (name + ".json")).write_text(json.dumps(value, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-libraries", type=Path)
    parser.add_argument("--candidate-libraries", type=Path)
    parser.add_argument("--baseline-revision")
    parser.add_argument("--candidate-revision")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet", help="Build SDK host")
    parser.add_argument("--runtime-dotnet", help="Execution host; defaults to --dotnet")
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--prepared", type=Path, help="Execute an existing snapshot; never build")
    parser.add_argument("--affinity", default="none")
    parser.add_argument("--allocation-repeats", type=int, default=5)
    args = parser.parse_args()
    if args.allocation_repeats < 1:
        parser.error("--allocation-repeats must be positive")
    if args.prepared and args.prepare_only:
        parser.error("--prepared and --prepare-only are mutually exclusive")
    if not args.prepared and not all((args.baseline_libraries, args.candidate_libraries,
                                      args.baseline_revision, args.candidate_revision)):
        parser.error("Preparation requires both library directories and revisions")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    if not args.prepared:
        prepare(args, output)
    if not args.prepare_only:
        measure(args, args.prepared.resolve(strict=True) if args.prepared else output, output)
    print(f"Completed diagnostic {'preparation' if args.prepare_only else 'comparison'}: {output}")


if __name__ == "__main__":
    main()
