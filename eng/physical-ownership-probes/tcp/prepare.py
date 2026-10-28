#!/usr/bin/env python3
"""Build only isolated probe sources; snapshot supplied runtime libraries without rebuilding them."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess


def hashes(directory):
    return {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(directory.glob("*.dll"))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-libraries", type=Path, required=True)
    parser.add_argument("--candidate-libraries", type=Path, required=True)
    parser.add_argument("--baseline-generator", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="New, non-existing directory")
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    baseline = args.baseline_libraries.resolve(strict=True)
    candidate = args.candidate_libraries.resolve(strict=True)
    generator = args.baseline_generator.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    source = Path(__file__).resolve().parent
    build = output / "build"
    build.mkdir()
    # Keep repository-level build settings from altering this standalone probe.
    for filename in ("Directory.Build.props", "Directory.Build.targets"):
        (build / filename).write_text("<Project />\n", encoding="utf-8")
    commands = []
    for name in ("module", "probe"):
        shutil.copytree(source / name, build / name,
                        ignore=shutil.ignore_patterns("bin", "obj"))
        command = [args.dotnet, "build", str(build / name / f"{name}.csproj"),
                   "-c", "Release", "-m:1", "-nr:false", "-p:UseSharedCompilation=false",
                   f"-p:LibraryRoot={baseline}",
                   f"-p:GeneratorPath={generator}", "--nologo", "-v", "minimal",
                   f"-bl:{output / (name + '-build.binlog')}"]
        commands.append(command)
        with (output / f"{name}-build.log").open("w", encoding="utf-8") as log:
            subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True)
    probe_output = build / "probe" / "bin" / "Release" / "net10.0"
    module = build / "module" / "bin" / "Release" / "net10.0" / "ScalarStreamModule.dll"
    for variant, library_root in (("baseline", baseline), ("candidate", candidate)):
        destination = output / variant
        shutil.copytree(probe_output, destination)
        shutil.copy2(module, destination / module.name)
        for library in destination.glob("*.dll"):
            if library.name not in ("PhysicalTcpProbe.dll", "ScalarStreamModule.dll"):
                supplied = library_root / library.name
                if not supplied.is_file():
                    raise FileNotFoundError(f"Missing {variant} dependency: {supplied}")
                shutil.copy2(supplied, library)
    for name in ("PhysicalTcpProbe.dll", "ScalarStreamModule.dll"):
        if (output / "baseline" / name).read_bytes() != (output / "candidate" / name).read_bytes():
            raise RuntimeError(f"Probe/module binary mismatch: {name}")
    baseline_hashes = hashes(output / "baseline")
    candidate_hashes = hashes(output / "candidate")
    for name in baseline_hashes:
        if not name.startswith("SharpLink.") and baseline_hashes[name] != candidate_hashes[name]:
            raise ValueError(f"Non-product binary mismatch: {name}")
    manifest = {
        "baselineLibraryDirectory": str(baseline),
        "candidateLibraryDirectory": str(candidate),
        "baselineGeneratorPath": str(generator),
        "baselineGeneratorSha256": hashlib.sha256(generator.read_bytes()).hexdigest(),
        "buildCommands": commands,
        "sourceHashes": {str(p.relative_to(source)): hashlib.sha256(p.read_bytes()).hexdigest()
                         for folder in ("module", "probe") for p in sorted((source / folder).glob("*"))
                         if p.is_file()},
        "baselineBinaryHashes": baseline_hashes,
        "candidateBinaryHashes": candidate_hashes,
    }
    (output / "prepared-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"Prepared isolated baseline/candidate snapshots: {output}")


if __name__ == "__main__":
    main()
