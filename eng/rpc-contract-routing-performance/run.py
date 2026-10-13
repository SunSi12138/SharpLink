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
RUNNER = "test/SharpLink.Benchmarks/ContractRoutingEvidenceRunner.cs"
PROGRAM = "test/SharpLink.Benchmarks/Program.cs"
ROUTE = '''        if (args.Length > 0 && string.Equals(
            args[0], "--contract-routing-evidence", StringComparison.Ordinal))
        {
            await ContractRoutingEvidenceRunner.RunAsync(args[1..]);
            return;
        }
'''
COMMON_INPUTS = (
    "global.json", "Directory.Build.props", "Directory.Packages.props",
    "test/Directory.Build.props", "eng/perf/allocation-budgets.json", PROJECT,
    "test/SharpLink.Benchmarks/BenchmarkEnvironment.cs",
    "test/SharpLink.Benchmarks/BenchmarkContracts.cs",
    "test/SharpLink.Benchmarks/BenchmarkService.cs",
    "test/SharpLink.Benchmarks/UnaryBenchmarks.cs",
    "test/SharpLink.Benchmarks/AllocationGateRunner.cs",
    "test/SharpLink.Benchmarks/AllocationSampleDiagnostics.cs",
    "test/SharpLink.Benchmarks/AllocationReadProbe.cs",
    "test/SharpLink.Benchmarks/SendPumpIdleWakeBenchmarks.cs",
)
SCENARIOS = {
    "unary": (1_024, 8_192),
    "oneway-sender": (4_096, 65_536),
    "oneway-receiver": (256, 2_048),
    "clientstream": (128, 1_024),
    "serverstream": (128, 1_024),
    "duplex": (128, 1_024),
}


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
            "SHARPLINK_BENCHMARK_SHA")},
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
    parser.add_argument("--expected-base", required=True)
    parser.add_argument("--expected-head", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    roots = {"base": args.base_root.resolve(), "head": args.head_root.resolve()}
    expected = {"base": args.expected_base, "head": args.expected_head}
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    failures = []
    provenance = {
        "schemaVersion": 1, "commits": expected, "order": ORDER,
        "hostName": platform.node(), "platform": platform.platform(),
        "machine": platform.machine(), "scenarios": SCENARIOS,
        "transports": ["tcp", "sharedmemory"], "commonInputSha256": {},
        "measurementBoundary": "BDN OneWay is sender-queued; focused receiver completion is observed independently; allocation gates retain their checked-in policy.",
    }
    save(output / "provenance.json", provenance)
    for arm, root in roots.items():
        if not re.fullmatch(r"[0-9a-f]{40}", expected[arm]) or git(root, "rev-parse", "HEAD") != expected[arm]:
            raise ValueError(f"{arm}: checkout is not the exact declared commit")
        if git(root, "status", "--porcelain"):
            raise ValueError(f"{arm}: performance checkout is not clean")
    for relative in COMMON_INPUTS:
        if (roots["base"] / relative).read_bytes() != (roots["head"] / relative).read_bytes():
            raise ValueError(f"Measurement input or policy changed between arms: {relative}")
        provenance["commonInputSha256"][relative] = digest(roots["head"] / relative)

    # One additive measurement file and its CLI route are the only baseline overlay.
    # Generated contracts, fixtures, budgets, dependency versions and production are untouched.
    head_program = (roots["head"] / PROGRAM).read_text(encoding="utf-8")
    base_program = (roots["base"] / PROGRAM).read_text(encoding="utf-8")
    if head_program.count(ROUTE) != 1 or head_program.replace(ROUTE, "") != base_program:
        raise ValueError("The benchmark entry-point diff contains more than the reviewed evidence route")
    (roots["base"] / RUNNER).write_bytes((roots["head"] / RUNNER).read_bytes())
    (roots["base"] / PROGRAM).write_bytes((roots["head"] / PROGRAM).read_bytes())
    provenance["runnerSha256"] = digest(roots["head"] / RUNNER)
    (output / "baseline-overlay.patch").write_text(git(roots["base"], "diff", "--", PROGRAM) + "\n", encoding="utf-8")
    (output / "ContractRoutingEvidenceRunner.cs").write_bytes((roots["head"] / RUNNER).read_bytes())
    for arm, root in roots.items():
        if git(root, "diff", "--", "src", "Directory.Build.props", "Directory.Packages.props", "global.json", "eng/perf/allocation-budgets.json"):
            raise ValueError(f"{arm}: production or policy was changed by the harness")

    cpus = sorted(os.sched_getaffinity(0))[:2]
    if len(cpus) != 2:
        raise ValueError("Matched evidence requires two available CPUs")
    affinity = ["taskset", "-c", ",".join(map(str, cpus))]
    environment = os.environ.copy()
    environment.update(DOTNET_PROCESSOR_COUNT="2", DOTNET_TieredCompilation="1", DOTNET_TieredPGO="1")
    versions = {arm: subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip()
                for arm, root in roots.items()}
    if len(set(versions.values())) != 1:
        raise ValueError(f"SDK versions differ: {versions}")
    provenance.update(cpuAffinity=cpus, sdkVersions=versions,
                      configuration="Release", dotnetProcessorCount=2,
                      tieredCompilation=1, tieredPgo=1,
                      bdn={"launchCount": 3, "warmupCount": 6,
                           "iterationCount": 15, "invocationCount": 4096,
                           "parameterLabels": ["PayloadSize=16", "PayloadSize=256"],
                           "parameterMeaning": "Both labels send identical OneWay arguments; neither is a payload-size comparison."})
    save(output / "provenance.json", provenance)
    execute(["dotnet", "--info"], roots["head"], output / "environment/dotnet", environment)
    execute(["lscpu"], roots["head"], output / "environment/cpu", environment)

    for arm, root in roots.items():
        for name, command in (
            ("restore", ["dotnet", "restore", PROJECT]),
            ("build", ["dotnet", "build", PROJECT, "-c", "Release", "--no-restore", "-v", "minimal"]),
        ):
            if not execute(command, root, output / "build" / arm / name, environment):
                raise RuntimeError(f"{arm} {name} failed; timing must not run against stale output")
        provenance.setdefault("assemblySha256", {})[arm] = digest(root / BENCHMARK)
    save(output / "provenance.json", provenance)

    for index, arm in enumerate(ORDER, 1):
        root = roots[arm]
        slot = output / "slots" / f"{index:02d}-{arm}"
        environment["SHARPLINK_BENCHMARK_SHA"] = expected[arm]
        if not execute(affinity + ["dotnet", "run", "--project", PROJECT, "-c", "Release",
                       "--no-build", "--no-restore", "--", "--filter", "*UnaryBenchmarks.Rpc_OnewayPublish*",
                       "--launchCount", "3", "--warmupCount", "6", "--iterationCount", "15",
                       "--invocationCount", "4096", "--exporters", "json", "--artifacts", str(slot / "bdn"),
                       "--keepFiles", "--buildTimeout", "180"], root, slot / "bdn-command", environment, 300):
            failures.append(f"{slot.name}/bdn")
        for transport in provenance["transports"]:
            for scenario, (warmup, operations) in SCENARIOS.items():
                case = slot / "rpc" / f"{transport}-{scenario}"
                command = affinity + ["dotnet", BENCHMARK, "--contract-routing-evidence", transport,
                                      scenario, str(warmup), str(operations), str(case / "result.json")]
                if not execute(command, root, case, environment, 120):
                    failures.append(f"{slot.name}/{case.name}")
        command = affinity + ["dotnet", BENCHMARK, "--allocation-gate",
                              "--budgets", str(root / "eng/perf/allocation-budgets.json"),
                              "--output", str(slot / "allocation/result.json")]
        if not execute(command, root, slot / "allocation", environment, 180):
            failures.append(f"{slot.name}/allocation")

    environment["SHARPLINK_BENCHMARK_SHA"] = expected["head"]
    negative = output / "negative-control"
    command = affinity + ["dotnet", BENCHMARK, "--allocation-gate", "--filter", "rpc-add-sharedmemory-c1",
                          "--inject-bytes-per-operation", "512",
                          "--budgets", str(roots["head"] / "eng/perf/allocation-budgets.json"),
                          "--output", str(negative / "result.json")]
    if not execute(command, roots["head"], negative, environment, 120, expected_failure=True):
        failures.append("allocation negative control did not fail")
    save(output / "execution-failures.json", failures)
    try:
        report = summarize(output)
        if not report["passedExecutionAndAllocationGates"]:
            failures.append("report validation or an unchanged allocation gate failed")
    except Exception as error:
        failures.append(f"summary rejected incomplete or inconsistent evidence: {error}")
    save(output / "execution-failures.json", failures)
    if failures:
        raise SystemExit("Performance evidence failed: " + "; ".join(failures))


if __name__ == "__main__":
    main()
