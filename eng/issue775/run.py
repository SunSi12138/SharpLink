#!/usr/bin/env python3
"""Run fixed-source ABBA/BAAB RPC evidence without altering production or allocation policy."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import signal
import subprocess
import time

from summarize import summarize

ORDER = ("base", "head", "head", "base", "head", "base", "base", "head")
PROJECT = "test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
BENCHMARK = "test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"
OVERLAY = (
    "test/SharpLink.Benchmarks/StaticBindingEvidence.cs",
    "test/SharpLink.Benchmarks/StaticBindingFixture.cs",
    "test/SharpLink.Benchmarks/BenchmarkEnvironment.cs",
    "test/SharpLink.Benchmarks/Program.cs",
    "test/SharpLink.Benchmarks/StaticBindingDialectGuard.cs",
)
COMMON_INPUTS = (
    "global.json", "Directory.Packages.props", "test/Directory.Build.props",
    "eng/perf/allocation-budgets.json", PROJECT,
    "test/SharpLink.Benchmarks/BenchmarkContracts.cs",
    "test/SharpLink.Benchmarks/BenchmarkService.cs",
    "test/SharpLink.Benchmarks/UnaryBenchmarks.cs",
    "test/SharpLink.Benchmarks/AllocationGateRunner.cs",
    "test/SharpLink.Benchmarks/AllocationSampleDiagnostics.cs",
    "test/SharpLink.Benchmarks/AllocationReadProbe.cs",
    "test/SharpLink.Benchmarks/SendPumpIdleWakeBenchmarks.cs",
    "eng/run-allocation-gate.sh",
)
WARMUP = 10000
OPERATIONS = 100000
LOADS = ((1, 1), (8, 4))


def git(root, *arguments):
    return subprocess.check_output(["git", "-C", str(root), *arguments], text=True).strip()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def execute(command, root, directory, environment, timeout=300, expected_failure=False):
    directory.mkdir(parents=True, exist_ok=True)
    record = {
        "argv": [str(value) for value in command], "cwd": str(root),
        "startedUnixSeconds": time.time(), "timeoutSeconds": timeout,
        "expectedFailure": expected_failure,
        "environment": {key: environment.get(key) for key in (
            "DOTNET_PROCESSOR_COUNT", "DOTNET_TieredCompilation", "DOTNET_TieredPGO",
            "SHARPLINK_BENCHMARK_SHA", "SHARPLINK_ALLOCATION_BUDGETS", "SHARPLINK_ALLOCATION_OUTPUT",
            "SharpLinkStaticBindingPrototype", "SHARPLINK_EXPECTED_MAGIC")},
    }
    save(directory / "command.json", record)
    print(f"RUN {directory.name}: {' '.join(record['argv'])}", flush=True)
    with (directory / "process.log").open("w", encoding="utf-8") as output:
        process = subprocess.Popen(record["argv"], cwd=root, env=environment,
                                   stdout=output, stderr=subprocess.STDOUT,
                                   start_new_session=True)
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            # BDN launches child processes. Stop this command's entire process group
            # before the next arm so a timed-out benchmark cannot contaminate it.
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait(timeout=5)
            code = 124
            output.write(f"\nEvidence command exceeded {timeout} seconds.\n")
    record.update(exitCode=code, completedUnixSeconds=time.time())
    save(directory / "command.json", record)
    (directory / "process-exit-code.txt").write_text(f"{code}\n", encoding="utf-8")
    return code != 0 if expected_failure else code == 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-root", type=Path, required=True)
    parser.add_argument("--head-root", type=Path, required=True)
    parser.add_argument("--expected-head", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    roots = {"base": args.base_root.resolve(), "head": args.head_root.resolve()}
    expected = {"base": "1a54993d3550911f5979a7b4d3f9f5f63ee86a64", "head": args.expected_head}
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    failures = []
    provenance = {
        "schemaVersion": 1, "commits": expected, "order": ORDER,
        "hostName": platform.node(), "platform": platform.platform(), "machine": platform.machine(),
        "warmup": WARMUP, "operations": OPERATIONS, "loads": LOADS,
        "transports": ["tcp", "sharedmemory"], "shapes": ["unary", "oneway"],
        "commonInputSha256": {}, "overlaySha256": {},
        "measurementBoundary": "OneWay sender-queued and receiver business completion are separate; no wire ACK."
    }
    save(output / "provenance.json", provenance)
    for arm, root in roots.items():
        if not re.fullmatch(r"[0-9a-f]{40}", expected[arm]) or git(root, "rev-parse", "HEAD") != expected[arm]:
            raise ValueError(f"{arm}: checkout does not match the declared commit")
        if git(root, "status", "--porcelain"):
            raise ValueError(f"{arm}: performance checkout is not clean")
    for relative in COMMON_INPUTS:
        if (roots["base"] / relative).read_bytes() != (roots["head"] / relative).read_bytes():
            raise ValueError(f"Original workload or allocation budget changed: {relative}")
        provenance["commonInputSha256"][relative] = digest(roots["head"] / relative)
    # Reviewed test-only overlay. The candidate-only Prepare blocks are compiled out in A.
    for relative in OVERLAY:
        content = (roots["head"] / relative).read_bytes()
        (roots["base"] / relative).write_bytes(content)
        provenance["overlaySha256"][relative] = digest(roots["head"] / relative)
        path = output / "harness" / Path(relative).name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
    (output / "baseline-overlay.patch").write_text(git(roots["base"], "diff") + "\n")
    if git(roots["base"], "diff", "--", "src", "Directory.Build.props", "global.json", "eng/perf/allocation-budgets.json"):
        raise ValueError("Harness changed baseline production or policy")
    cpus = sorted(os.sched_getaffinity(0))[:2]
    if len(cpus) != 2:
        raise ValueError("Two available CPUs are required for matched timing")
    affinity = ["taskset", "-c", ",".join(map(str, cpus))]
    canonical = os.environ.copy()
    timing = canonical | {"DOTNET_PROCESSOR_COUNT": "2", "DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "1"}
    versions = {arm: subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip()
                for arm, root in roots.items()}
    if len(set(versions.values())) != 1:
        raise ValueError("SDK versions differ")
    provenance.update(cpuAffinity=cpus, sdkVersions=versions, configuration="Release", dotnetProcessorCount=2,
                      tieredCompilation=1, tieredPgo=1,
                      allocationExecution={"entryPoint": "eng/run-allocation-gate.sh",
                          "cpuAffinity": sorted(os.sched_getaffinity(0)),
                          "environment": {k: canonical.get(k) for k in ("DOTNET_PROCESSOR_COUNT", "DOTNET_TieredCompilation", "DOTNET_TieredPGO")}},
                      bdn={"launchCount": 3, "warmupCount": 6, "iterationCount": 15, "invocationCount": 4096,
                           "parameterLabels": ["PayloadSize=16", "PayloadSize=256"],
                           "parameterMeaning": "Both labels execute the unchanged identical OneWay expression; PayloadSize is not used by that method."})
    save(output / "provenance.json", provenance)
    execute(["dotnet", "--info"], roots["head"], output / "environment/dotnet", timing)
    execute(["lscpu"], roots["head"], output / "environment/cpu", timing)

    def arm_environment(arm, initial):
        return initial | {"SharpLinkStaticBindingPrototype": "true" if arm == "head" else "false",
                          "SHARPLINK_BENCHMARK_SHA": expected[arm],
                          "SHARPLINK_EXPECTED_MAGIC": "183" if arm == "head" else "137"}

    for arm, root in roots.items():
        env = arm_environment(arm, timing)
        for name, command in (("restore", ["dotnet", "restore", PROJECT]),
                              ("build", ["dotnet", "build", PROJECT, "-c", "Release", "--no-restore", "-v", "minimal"])):
            if not execute(command, root, output / "build" / arm / name, env):
                raise RuntimeError(f"{arm} {name} failed; refusing stale binaries")
        provenance.setdefault("assemblySha256", {})[arm] = digest(root / BENCHMARK)
    save(output / "provenance.json", provenance)
    if not execute(["dotnet", BENCHMARK, "--static-binding-smoke"], roots["head"],
                   output / "smoke", arm_environment("head", canonical), 120):
        raise RuntimeError("Real-transport safety checks failed; timing blocked")
    for index, arm in enumerate(ORDER, 1):
        root = roots[arm]
        slot = output / "slots" / f"{index:02d}-{arm}"
        env = arm_environment(arm, timing)
        # Original queue-only benchmark and hierarchy; do not replace it with receiver throughput.
        command = affinity + ["dotnet", "run", "--project", PROJECT, "-c", "Release", "--no-build", "--no-restore", "--",
                              "--filter", "*UnaryBenchmarks.Rpc_OnewayPublish*", "--launchCount", "3",
                              "--warmupCount", "6", "--iterationCount", "15", "--invocationCount", "4096",
                              "--exporters", "json", "--artifacts", str(slot / "bdn"), "--keepFiles", "--buildTimeout", "180"]
        if not execute(command, root, slot / "bdn-command", env, 300):
            failures.append(f"{slot.name}/bdn")
        for transport in provenance["transports"]:
            for shape in provenance["shapes"]:
                for concurrency, pool in LOADS:
                    case = slot / "rpc" / f"{transport}-{shape}-c{concurrency}-p{pool}"
                    command = affinity + ["dotnet", BENCHMARK, "--static-binding-evidence", transport, shape,
                                          str(concurrency), str(pool), str(WARMUP), str(OPERATIONS), str(case / "result.json")]
                    if not execute(command, root, case, env, 120):
                        failures.append(f"{slot.name}/{case.name}")
        gate_env = arm_environment(arm, canonical) | {
            "SHARPLINK_ALLOCATION_BUDGETS": str(root / "eng/perf/allocation-budgets.json"),
            "SHARPLINK_ALLOCATION_OUTPUT": str(slot / "allocation/result.json")}
        if not execute(["bash", "eng/run-allocation-gate.sh"], root, slot / "allocation", gate_env, 180):
            failures.append(f"{slot.name}/allocation")
    negative = output / "negative-control"
    env = arm_environment("head", canonical) | {
        "SHARPLINK_ALLOCATION_BUDGETS": str(roots["head"] / "eng/perf/allocation-budgets.json"),
        "SHARPLINK_ALLOCATION_OUTPUT": str(negative / "result.json")}
    if not execute(["bash", "eng/run-allocation-gate.sh", "--filter", "rpc-add-sharedmemory-c1",
                    "--inject-bytes-per-operation", "512"], roots["head"], negative, env, 120, expected_failure=True):
        failures.append("allocation negative control did not fail")
    save(output / "execution-failures.json", failures)
    try:
        result = summarize(output)
        if not result["passedExecutionAndAllocationGates"]:
            failures.append("Unchanged allocation gates or evidence validation failed")
    except Exception as error:
        failures.append(f"Incomplete or inconsistent evidence: {error}")
    save(output / "execution-failures.json", failures)
    if failures:
        raise SystemExit("Evidence failed: " + "; ".join(failures))


if __name__ == "__main__":
    main()
