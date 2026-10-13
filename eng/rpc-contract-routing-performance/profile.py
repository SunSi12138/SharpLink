#!/usr/bin/env python3
"""Capture diagnostic CPU stacks and JIT output for an exact production pair."""

import argparse
import os
from pathlib import Path
import platform
import re
import subprocess

from run import BENCHMARK, COMMON_INPUTS, PROJECT, digest, execute, git, save
from profile_summary import summarize_profiles

ORDER = ("base", "head", "head", "base")
JIT_METHODS = " ".join((
    "*SharpLinkClient:SendRpcCall*", "*SharpLinkClient:InvokeOneWay*", "*InvokeOneWayPlainCoreAsync*:MoveNext",
    "*SharpLinkContractRpcChannel:InvokeOneWay*", "*ClientContractRouteTable:TryGetRoute*",
    "*ClientContractRoute:SelectConnection*", "*ProtocolV2RequestIdentityCodec:*",
    "*PendingCall:*", "*RequestAdmissionWaiter:*", "*SharpLinkServer:CreateCallContext*",
    "*SharpLinkServer:InvokeAdmittedServiceAsync*", "*StreamCallContextSnapshot:*",
    "*ServerContractAdmission:*", "*DecodedRequestEnvelope:*", "*ServerRequestEnvelopeReader:Read*",
))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-root", type=Path, required=True)
    parser.add_argument("--head-root", type=Path, required=True)
    parser.add_argument("--expected-base", required=True)
    parser.add_argument("--expected-head", required=True)
    parser.add_argument("--driver-sha", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    roots = {"base": args.base_root.resolve(), "head": args.head_root.resolve()}
    commits = {"base": args.expected_base, "head": args.expected_head}
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    cpus = sorted(os.sched_getaffinity(0))[:2]
    if len(cpus) != 2:
        raise ValueError("Matched profiling requires two available CPUs")
    affinity = ["taskset", "-c", ",".join(map(str, cpus))]
    environment = os.environ.copy()
    environment.update(DOTNET_PROCESSOR_COUNT="2", DOTNET_TieredCompilation="1", DOTNET_TieredPGO="1")
    provenance = {
        "schemaVersion": 1, "driverSha": args.driver_sha, "commits": commits,
        "hostName": platform.node(), "platform": platform.platform(), "order": ORDER,
        "cpuAffinity": cpus, "dotnetProcessorCount": 2, "tieredCompilation": 1, "tieredPgo": 1,
        "commonInputSha256": {}, "assemblySha256": {},
        "cpuProfiler": "BenchmarkDotNet EventPipe CPU sampling (EP)",
        "bdn": {"launchesPerSlot": 1, "warmupCount": 6, "actualIterations": 15, "invocations": 4096},
        "jitDisasmMethods": JIT_METHODS,
        "scope": "Diagnostic attribution only. CPU-profiled and disassembly timings are excluded from all performance acceptance ratios.",
    }
    save(output / "provenance.json", provenance)
    for arm, root in roots.items():
        if not re.fullmatch(r"[0-9a-f]{40}", commits[arm]) or git(root, "rev-parse", "HEAD") != commits[arm]:
            raise ValueError(f"{arm}: checkout does not match the declared exact production commit")
        if git(root, "status", "--porcelain"):
            raise ValueError(f"{arm}: production checkout is not clean")
    for relative in COMMON_INPUTS:
        if (roots["base"] / relative).read_bytes() != (roots["head"] / relative).read_bytes():
            raise ValueError(f"Matched diagnostic input changed: {relative}")
        provenance["commonInputSha256"][relative] = digest(roots["head"] / relative)
    versions = {arm: subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip()
                for arm, root in roots.items()}
    if len(set(versions.values())) != 1:
        raise ValueError("Diagnostic SDK versions differ")
    provenance["sdkVersions"] = versions
    save(output / "provenance.json", provenance)
    execute(["dotnet", "--info"], roots["head"], output / "environment/dotnet", environment)
    execute(["lscpu"], roots["head"], output / "environment/cpu", environment)
    for arm, root in roots.items():
        for name, command in (
            ("restore", ["dotnet", "restore", PROJECT]),
            ("build", ["dotnet", "build", PROJECT, "-c", "Release", "--no-restore", "-v", "minimal"]),
        ):
            if not execute(command, root, output / "build" / arm / name, environment):
                raise RuntimeError(f"{arm}: diagnostic {name} failed")
        provenance["assemblySha256"][arm] = digest(root / BENCHMARK)
    save(output / "provenance.json", provenance)

    failures = []
    common = ["dotnet", "run", "--project", PROJECT, "-c", "Release", "--no-build", "--no-restore",
              "--", "--filter", "*UnaryBenchmarks.Rpc_OnewayPublish*", "--launchCount", "1",
              "--warmupCount", "6", "--iterationCount", "15", "--invocationCount", "4096",
              "--exporters", "json", "--keepFiles", "--buildTimeout", "180"]
    for index, arm in enumerate(ORDER, 1):
        slot = output / "profiles" / f"{index:02d}-{arm}"
        environment["SHARPLINK_BENCHMARK_SHA"] = commits[arm]
        command = affinity + common + ["--profiler", "EP", "--artifacts", str(slot / "bdn")]
        if not execute(command, roots[arm], slot, environment, 240):
            failures.append(f"{slot.name}: CPU profiler failed")

    # JIT output is a separate diagnostic process. It cannot perturb the CPU
    # profiles above or enter the unprofiled timing comparison in run.py.
    for arm, root in roots.items():
        directory = output / "disassembly" / arm
        disassembly_environment = environment.copy()
        disassembly_environment.update(SHARPLINK_BENCHMARK_SHA=commits[arm],
                                       DOTNET_JitDisasm=JIT_METHODS, DOTNET_JitDisasmDiffable="1")
        save(directory / "jit-environment.json", {
            "DOTNET_JitDisasm": JIT_METHODS, "DOTNET_JitDisasmDiffable": "1",
            "reference": "https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/viewing-jit-dumps.md",
        })
        if not execute(affinity + common + ["--artifacts", str(directory / "bdn")],
                       root, directory, disassembly_environment, 240):
            failures.append(f"{arm}: JIT diagnostic failed")
        logs = list((directory / "bdn").rglob("*.log")) + [directory / "process.log"]
        listings = sum(path.read_text(encoding="utf-8", errors="replace").count("Assembly listing for method")
                       for path in logs if path.exists())
        if listings == 0:
            failures.append(f"{arm}: no JIT assembly was captured")
    # Short-stream CPU captures and JIT processes are separate from unprofiled ablations.
    stream_common = ["dotnet", "run", "--project", PROJECT, "-c", "Release", "--no-build", "--no-restore",
                     "--", "--filter", "*StreamingBenchmarks.Rpc_ClientStreamUpload*", "*StreamingBenchmarks.Rpc_DuplexStream*",
                     "--launchCount", "1", "--warmupCount", "3", "--iterationCount", "6", "--invocationCount", "1024",
                     "--exporters", "json", "--keepFiles", "--buildTimeout", "180"]
    for index, arm in enumerate(ORDER, 1):
        slot = output / "stream-profiles" / f"{index:02d}-{arm}"
        environment["SHARPLINK_BENCHMARK_SHA"] = commits[arm]
        if not execute(affinity + stream_common + ["--profiler", "EP", "--artifacts", str(slot / "bdn")],
                       roots[arm], slot, environment, 360):
            failures.append(f"{slot.name}: stream CPU profiler failed")
        if not list(slot.rglob("*.nettrace")) or not list(slot.rglob("*.speedscope.json")):
            failures.append(f"{slot.name}: missing stream CPU trace")
    for arm, root in roots.items():
        directory = output / "stream-disassembly" / arm
        jit_environment = environment.copy()
        jit_environment.update(SHARPLINK_BENCHMARK_SHA=commits[arm],
                               DOTNET_JitDisasm=JIT_METHODS, DOTNET_JitDisasmDiffable="1")
        if not execute(affinity + stream_common + ["--artifacts", str(directory / "bdn")],
                       root, directory, jit_environment, 360):
            failures.append(f"{arm}: stream JIT diagnostic failed")
        logs = list(directory.rglob("*.log"))
        if not any("Assembly listing for method" in path.read_text(errors="replace") for path in logs):
            failures.append(f"{arm}: missing stream JIT assembly")
    try:
        summarize_profiles(output)
    except Exception as error:
        failures.append(f"Profile validation failed: {error}")
    save(output / "execution-failures.json", failures)
    if failures:
        raise SystemExit("Diagnostic evidence failed: " + "; ".join(failures))


if __name__ == "__main__":
    main()
