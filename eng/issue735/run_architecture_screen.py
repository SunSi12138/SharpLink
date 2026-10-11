#!/usr/bin/env python3
"""Stage-one matched generated RPC screen. Collection success is not acceptance.

Each runtime runs A/B on one machine, one CPU affinity, unchanged default windows,
the same generated contract/codec and the same measurement source. All processes,
failures, stdout/stderr, binaries and per-RPC samples are retained. No rerun-until-
green logic, fallback scheduler, changed socket settings or benchmark threshold.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

BASE = "f8ba89121c87a6410a0139c44d7db30eab877f41"
PROJECT = "test/SharpLink.FlowArchitectureEvidence/SharpLink.FlowArchitectureEvidence.csproj"
HARNESS = "test/SharpLink.FlowArchitectureEvidence"

# A small falsification screen precedes any broad production matrix. Fixed work
# counts, including all negative controls, are identical between A and B.
# (shape, item count, payload bytes, concurrency, measured RPCs per worker)
CASES = [
    ("Unary", 1, 16, 1, 32768),
    ("Server", 1, 16, 1, 16384),
    ("Duplex", 100, 16, 1, 2048),
    ("Server", 1000, 16, 128, 32),
    ("Client", 1000, 16, 128, 32),
    ("Duplex", 1000, 16, 32, 64),
    ("Duplex", 1000, 16, 128, 32),
    ("Duplex", 100, 4096, 8, 128),
]
AA_CASES = [("Duplex", 100, 16, 1, 2048), ("Duplex", 1000, 16, 128, 32)]


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", choices=["jit-pgo0", "jit-pgo1", "nativeaot"], required=True)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    repo = Path.cwd()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    cpus = sorted(os.sched_getaffinity(0))[:4]
    if len(cpus) != 4:
        raise RuntimeError("The predefined screen requires four available processors")
    affinity = ",".join(map(str, cpus))
    rows = []

    def execute(command, label, cwd=repo, env=None, timeout=600):
        started = time.time()
        with (out / (label + ".stdout.log")).open("w") as stdout, \
                (out / (label + ".stderr.log")).open("w") as stderr:
            try:
                code = subprocess.run(command, cwd=cwd, env=env, stdout=stdout,
                                      stderr=stderr, timeout=timeout).returncode
            except subprocess.TimeoutExpired:
                code = 124
        rows.append({"name": label, "command": command, "cwd": str(cwd),
                     "exitCode": code, "utcStartEpochSeconds": started,
                     "wallSeconds": time.time() - started})
        (out / "exits.json").write_text(json.dumps(rows, indent=2) + "\n")
        print(json.dumps({"name": label, "exitCode": code}), flush=True)
        return code

    manifest = {"classification": "stage1-real-generated-RPC-not-full-acceptance",
                "baselineSha": BASE, "candidateSha": head, "measurementRecipeSha": head,
                "runtimePopulation": args.runtime, "cpuAffinity": cpus,
                "cases": CASES, "aaCases": AA_CASES,
                "abOrders": [["A", "B", "B", "A"], ["B", "A", "A", "B"]],
                "sourceTrees": {}}
    (out / "screen-plan.json").write_text(json.dumps(manifest, indent=2) + "\n")
    for command, label in [(["dotnet", "--info"], "dotnet-info"),
                           (["lscpu"], "lscpu"), (["uname", "-a"], "kernel")]:
        if execute(command, label) != 0:
            raise RuntimeError("Environment recording failed: " + label)
    if execute(["git", "fetch", "--no-tags", "--depth=1", "origin", BASE], "fetch-base") != 0:
        raise RuntimeError("Cannot fetch the pinned baseline")

    subjects = {}
    for mode, revision, variant in [("A", BASE, "baseline"), ("B", head, "D-split-direction-gates")]:
        work = Path(os.environ["RUNNER_TEMP"]) / ("issue735-" + args.runtime + "-" + mode)
        if execute(["git", "worktree", "add", "--detach", str(work), revision], "worktree-" + mode) != 0:
            raise RuntimeError("Cannot create isolated subject " + mode)
        target = work / HARNESS
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(repo / HARNESS, target)
        identity = out / ("identity-" + mode + ".json")
        # This source-verifying stamper refuses an unrecognized controller and
        # checks that the original SendPump and the shared harness are unchanged.
        runtime_tree = subprocess.check_output(
            ["git", "rev-parse", revision + "^{tree}"], cwd=work, text=True).strip()
        command = ["python3", str(target / "stamp_identity.py"),
                   "--root", str(work), "--mode", variant,
                   "--baseline-sha", BASE, "--candidate-sha", revision,
                   "--runtime-source-tree-sha", runtime_tree, "--record", str(identity)]
        if execute(command, "identity-" + mode) != 0:
            raise RuntimeError("Source identity verification failed: " + mode)
        info = json.loads(identity.read_text())
        manifest["sourceTrees"][mode] = info
        (out / "screen-plan.json").write_text(json.dumps(manifest, indent=2) + "\n")
        (out / ("source-" + mode + ".patch")).write_bytes(
            subprocess.check_output(["git", "diff", "--binary"], cwd=work))
        binary_dir = out / "binaries" / mode
        binary_dir.mkdir(parents=True)
        if args.runtime == "nativeaot":
            command = ["dotnet", "publish", PROJECT, "-c", "Release", "-r", "linux-x64",
                       "-p:PublishAot=true", "-o", str(binary_dir), "-v", "minimal"]
            binary = binary_dir / "SharpLink.Benchmarks"
            prefix = ["taskset", "-c", affinity, str(binary)]
        else:
            command = ["dotnet", "build", PROJECT, "-c", "Release",
                       "-p:PublishAot=false", "-v", "minimal"]
            binary = binary_dir / "SharpLink.Benchmarks.dll"
            prefix = ["taskset", "-c", affinity, "dotnet", str(binary)]
        if execute(command, "build-" + mode, work, timeout=900) != 0:
            raise RuntimeError("Build failed: " + mode)
        if args.runtime != "nativeaot":
            shutil.copytree(target / "bin/Release/net10.0", binary_dir, dirs_exist_ok=True)
        if not binary.is_file():
            raise RuntimeError("The measured binary does not exist")
        subjects[mode] = (prefix, info, variant)
        manifest["sourceTrees"][mode]["binarySha256"] = sha256(binary)
        (out / "screen-plan.json").write_text(json.dumps(manifest, indent=2) + "\n")

    for key in ["MeasurementSourceSha", "SendPumpSourceSha256"]:
        if subjects["A"][1][key] != subjects["B"][1][key]:
            raise RuntimeError("A/B do not use the same shared source: " + key)

    env = dict(os.environ, DOTNET_PROCESSOR_COUNT="4", DOTNET_ReadyToRun="0",
               DOTNET_TieredCompilation="1", DOTNET_TieredPGO="0" if args.runtime == "jit-pgo0" else "1")
    if args.runtime == "nativeaot":
        # JIT flags have no effect on native code; do not label an AOT population PGO ON.
        env.pop("DOTNET_TieredPGO", None)
        env.pop("DOTNET_TieredCompilation", None)

    def measured(case, transport, mode, label):
        shape, items, size, concurrency, operations = case
        prefix, info, variant = subjects[mode]
        result = out / "samples" / (label + ".txt")
        result.parent.mkdir(exist_ok=True)
        command = prefix + [transport, shape, str(items), str(size), str(concurrency),
                            "2", str(operations), BASE, info["RuntimeSourceTreeSha"],
                            variant, str(result)]
        code = execute(command, label, env=env, timeout=60)
        if code != 0:
            return False
        values = dict(line.split("=", 1) for line in result.read_text(encoding="utf-8-sig").splitlines()
                      if "=" in line)
        required = {"status": "passed", "compiledMode": variant, "baselineSha": BASE,
                    "candidateSha": info["CandidateSha"],
                    "measurementSourceSha": info["MeasurementSourceSha"],
                    "runtimeSourceTreeSha": info["RuntimeSourceTreeSha"],
                    "samePhysicalSessionBeforeAndAfter": "true",
                    "productionScheduler": "dev-SendPump", "b3ReadyWriterEnabled": "false"}
        for key, value in required.items():
            if values.get(key) != value:
                raise RuntimeError("Report provenance/validation mismatch: " + label + ":" + key)
        actual_aot = values["builtForNativeAot"] == "true"
        if actual_aot != (args.runtime == "nativeaot"):
            raise RuntimeError("The executed runtime is not the requested population")
        if values["measuredBinarySha256"] != manifest["sourceTrees"][mode]["binarySha256"]:
            raise RuntimeError("The measured binary hash changed")
        return True

    failed = []

    def stop_after_failure(label):
        failed.append(label)
        (out / "collection-result.json").write_text(json.dumps({
            "failedProcesses": failed, "processRows": len(rows),
            "collectionSucceeded": False, "productionGo": False,
            "stoppedAtFirstFailure": True,
            "note": "Remaining predeclared cells were not run; inspect failure before more optimization."
        }, indent=2) + "\n")
        raise RuntimeError("RPC process failed; stop at correctness/progress boundary: " + label)

    for round_index, order in enumerate(manifest["abOrders"]):
        # Round two reverses cell traversal as well as binary order.
        cases = CASES if round_index == 0 else list(reversed(CASES))
        for transport in (["tcp", "sharedmemory"] if round_index == 0 else ["sharedmemory", "tcp"]):
            for case in cases:
                name = f"{transport}-{case[0]}-{case[1]}x{case[2]}-c{case[3]}"
                for position, mode in enumerate(order):
                    label = f"ab-r{round_index}-{name}-p{position}-{mode}"
                    if not measured(case, transport, mode, label):
                        stop_after_failure(label)
    for transport in ["tcp", "sharedmemory"]:
        for case in AA_CASES:
            name = f"{transport}-{case[0]}-{case[1]}x{case[2]}-c{case[3]}"
            for pair in range(2):
                for label_side in (["left", "right"] if pair == 0 else ["right", "left"]):
                    label = f"aa-r{pair}-{name}-{label_side}"
                    if not measured(case, transport, "A", label):
                        stop_after_failure(label)
    (out / "collection-result.json").write_text(json.dumps({
        "failedProcesses": failed, "processRows": len(rows),
        "collectionSucceeded": not failed, "productionGo": False,
        "note": "All predeclared processes retained. Interpret A/A noise, CPU, allocation and tails before any next stage."
    }, indent=2) + "\n")
    if failed:
        raise RuntimeError(f"{len(failed)} measured processes failed; evidence retained")


if __name__ == "__main__":
    main()
