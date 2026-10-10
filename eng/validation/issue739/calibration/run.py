#!/usr/bin/env python3
"""Run isolated source-shape calibrations; no end-to-end owner-coverage claim."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import subprocess

CASES = ["logical-incomplete-wrapper", "logical-completion-control", "logical-completed-wrapper",
         "permit-plain", "permit-capacity-control", "context-null-transition", "context-null-control",
         "context-same-snapshot", "context-flow-transition", "context-flow-null-control"]
SOURCE = "eb99fe887cf2129d9b88441245ca0a4a6406b6c2"


def sha(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--dotnet", required=True, help="dotnet executable or runtime-enforcing wrapper")
    p.add_argument("--output", required=True)
    p.add_argument("--samples", type=int, default=5)
    p.add_argument("--operations", type=int, default=131072)
    p.add_argument("--warmup", type=int, default=16384)
    p.add_argument("--runtime", default="10.0.12")
    p.add_argument("--timeout", type=int, default=60)
    p.add_argument("--correctness-only", action="store_true", help="allow a different local runtime; never acceptance evidence")
    args = p.parse_args()
    if not 5 <= args.samples <= 10 and not (args.correctness_only and 1 <= args.samples <= 10):
        p.error("Use 5..10 fresh samples; correctness-only may use 1")
    if args.runtime != "10.0.12" and not args.correctness_only:
        p.error("Hosted evidence requires runtime 10.0.12")
    if not 1 <= args.operations <= 1048576 or not 1 <= args.warmup <= 65536 or not 1 <= args.timeout <= 180:
        p.error("Counts/timeout outside bounded range")
    here = Path(__file__).resolve().parent
    root = here.parents[3]
    executable = here / "bin/Release/net10.0/SharpLink.Benchmarks.dll"
    if not executable.is_file():
        p.error("Build Calibration.csproj in Release before running")
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if list(output.glob("*.sample.json")):
        p.error("Use a fresh output directory")
    subprocess.run(["git", "diff", "--exit-code", SOURCE, "--", "src", "Directory.Build.props", "Directory.Packages.props", "global.json"], cwd=root, check=True, stdout=subprocess.PIPE)
    if subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard", "src"], cwd=root, text=True).strip():
        raise ValueError("Unexpected untracked production source files")
    env = dict(os.environ, ISSUE739_SOURCE_SHA=SOURCE)
    source_paths = list((root / "src").rglob("*.cs")) + list((root / "src").rglob("*.csproj"))
    source_paths = [x for x in source_paths if not {"bin", "obj"}.intersection(x.parts)]
    source_paths += list(here.glob("*.cs")) + list(here.glob("*.csproj")) + list(here.glob("*.py"))
    source_paths += [root / "Directory.Build.props", root / "Directory.Packages.props", root / "global.json"]
    binary_paths = [x for x in executable.parent.iterdir() if x.is_file() and (x.suffix in (".dll", ".json", ".pdb"))]
    provenance = {"schemaVersion": 1, "sourceSha": SOURCE, "runtimeRequired": args.runtime,
                  "correctnessOnly": args.correctness_only,
                  "cpuInfo": Path("/proc/cpuinfo").read_text() if Path("/proc/cpuinfo").exists() else "unavailable",
                  "runtimeEnvironment": {key: env.get(key, "runtime-default") for key in ("DOTNET_TieredPGO", "DOTNET_TieredCompilation", "DOTNET_ReadyToRun", "DOTNET_gcServer", "COMPlus_gcServer")},
                  "gitHead": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
                  "dotnetInfo": subprocess.check_output([args.dotnet, "--info"], text=True),
                  "sourceHashes": {str(x.relative_to(root)): sha(x) for x in sorted(source_paths)},
                  "executableAndConfigHashes": {x.name: sha(x) for x in sorted(binary_paths)},
                  "processPolicy": "fresh process for every case/sample; cases rotate by repetition; no raw-control subtraction"}
    (output / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    results = []
    for repetition in range(args.samples):
        order = CASES[repetition % len(CASES):] + CASES[:repetition % len(CASES)]
        for position, case in enumerate(order, 1):
            sample = f"{case}-r{repetition+1}"
            target = output / (sample + ".sample.json")
            command = [args.dotnet, str(executable), case, str(args.operations), str(args.warmup), sample, str(target), args.runtime]
            with open(output / (sample + ".log"), "w") as log:
                subprocess.run(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT,
                               check=True, timeout=args.timeout)
            row = json.loads(target.read_text())
            if row["kind"] != case or row["sample"] != sample or row["operations"] != args.operations or row["warmup"] != args.warmup:
                raise ValueError("Case/count identity mismatch")
            if row["expectedRuntime"] != args.runtime:
                raise ValueError("Expected runtime mismatch")
            for key in ("bytes", "currentThreadBytes"):
                if type(row[key]) is not int or row[key] < 0:
                    raise ValueError("Invalid byte count")
            for key, byte_key in (("bytesPerOperation", "bytes"), ("currentThreadBytesPerOperation", "currentThreadBytes")):
                if not math.isfinite(row[key]) or row[key] < 0 or row[key] != row[byte_key] / args.operations:
                    raise ValueError("Invalid normalized allocation")
            expected_incomplete = args.operations if case in ("logical-incomplete-wrapper", "logical-completion-control") else 0
            if row["startedIncomplete"] != expected_incomplete:
                raise ValueError("Incomplete invocation count mismatch")
            if case.startswith("context-flow-") and row["details"]["flowCallbackChecks"] != args.operations:
                raise ValueError("Flow callback count mismatch")
            if row["sourceSha"] != SOURCE or row["runtime"] != args.runtime or row["checks"] != args.operations or row["completed"] != args.operations:
                raise ValueError("Sample identity/count mismatch")
            if row["executableSha256"] != provenance["executableAndConfigHashes"][executable.name]:
                raise ValueError("Executed binary hash mismatch")
            if row["threadStart"] != row["threadEnd"]:
                raise ValueError("Synchronous calibration changed thread")
            row["repetition"] = repetition + 1
            row["position"] = position
            row["sampleSha256"] = sha(target)
            results.append(row)
            print(sample, f'{row["bytesPerOperation"]:.6f} process B/op', flush=True)
    if len({x["processId"] for x in results}) != len(results):
        raise ValueError("PIDs not unique")
    summaries = []
    for case in CASES:
        rows = [x for x in results if x["kind"] == case]
        values = [x["bytesPerOperation"] for x in rows]
        summaries.append({"kind": case, "samples": len(rows), "processBytesPerOperation": {"min": min(values), "median": statistics.median(values), "max": max(values)},
                          "currentThreadBytesPerOperation": [x["currentThreadBytesPerOperation"] for x in rows]})
    (output / "summary.json").write_text(json.dumps({"schemaVersion": 1, "sourceSha": SOURCE, "correctnessOnly": args.correctness_only, "runtime": args.runtime,
        "provenanceSha256": sha(output / "provenance.json"), "interpretation": "source-shape microcontrols; no subtraction or owner90 closure", "cases": summaries, "samples": results}, indent=2) + "\n")


if __name__ == "__main__":
    main()
