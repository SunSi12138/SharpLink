#!/usr/bin/env python3
"""Prepare the fixed three-arm suite on its disposable Linux runner; never time RPCs."""
import argparse
import datetime
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
from runtime_contract import RUNTIME, SDK, validate_metadata
from spec import CALLS, CELLS, CONTROLLED, PROFILES, SAMPLES, WARMUP

REVISIONS = {
    "O": "a6bd0352a931d5c2816e42778055e90efb01144b",
    "A": "37548b7a35d50803212d2b0a637306636cc0a1d3",
    "B": "2756e8810772cf956ee70ed380925ecc86e86a32",
}
ABI = {"O": "sharplink-3.0-api5-owner-bound-unsafe-blit-v1",
       "A": "sharplink-3.0-api5-complete-method-facts-v1",
       "B": "sharplink-3.0-api5-complete-method-facts-v1"}


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def write(path, data):
    path.write_text(json.dumps(data, indent=2) + "\n")


def capture(command, cwd=None):
    return subprocess.check_output(list(map(str, command)), cwd=cwd, text=True).strip()


def verify_source(root, expected):
    actual = capture(["git", "rev-parse", "HEAD"], root)
    status = capture(["git", "status", "--porcelain"], root)
    if actual != expected or status:
        raise ValueError(f"Expected clean source {expected} at {root}; got {actual}: {status}")
    return {"path": str(root), "commit": actual, "clean": True}


def select_cpus(topology):
    selected, cores = [], set()
    for row in sorted(topology, key=lambda r: r["cpu"]):
        core = (row["package"], row["core"])
        if min(core) < 0:
            raise ValueError("CPU physical topology is unknown; no affinity fallback allowed")
        if core not in cores:
            selected.append(row["cpu"])
            cores.add(core)
            if len(selected) == 2:
                return selected
    raise ValueError("Need two allowed CPUs on distinct reported physical cores; no SMT-only/one-core fallback")


def topology_snapshot():
    available = sorted(os.sched_getaffinity(0))
    rows = []
    for cpu in available:
        directory = Path(f"/sys/devices/system/cpu/cpu{cpu}/topology")
        rows.append({"cpu": cpu, "package": int((directory / "physical_package_id").read_text()),
                     "core": int((directory / "core_id").read_text()),
                     "threadSiblings": (directory / "thread_siblings_list").read_text().strip()})
    return {"availableCpus": available, "topology": rows, "selectedCpus": select_cpus(rows)}


def validate_runtime_listing(dotnet, runtimes, sdks):
    lines = runtimes.splitlines()
    expected_frameworks = {("Microsoft.NETCore.App", RUNTIME), ("Microsoft.AspNetCore.App", RUNTIME)}
    if {tuple(line.split()[:2]) for line in lines} != expected_frameworks or len(lines) != 2 or sdks.strip():
        raise ValueError(f"Private runtime-only .NETCore + AspNet {RUNTIME} required; no SDK/other patch: {runtimes}")
    for line in lines:
        expected = dotnet.parent / "shared" / line.split()[0]
        if Path(line.split("[", 1)[1].rstrip("]")).resolve() != expected:
            raise ValueError("Framework path escaped private runtime installation")


def pin_runtimeconfig(path):
    data = json.loads(path.read_text())
    options = data["runtimeOptions"]
    frameworks = options.get("frameworks", [options.get("framework")])
    if len(frameworks) != 1 or frameworks[0]["name"] != "Microsoft.NETCore.App":
        raise ValueError(f"Expected only .NETCore framework: {path}")
    frameworks[0]["version"] = RUNTIME
    options["rollForward"] = "Disable"
    write(path, data)
    return data


def verify_dependencies(apps):
    inventories = {arm: {p.name: p for p in app.glob("*.dll")} for arm, app in apps.items()}
    base = inventories["O"]
    for arm, files in inventories.items():
        if files.keys() != base.keys():
            raise ValueError(f"Runtime DLL inventory differs in arm {arm}")
        for name in files:
            if name != "CausalProbe.dll" and not name.startswith("SharpLink."):
                if files[name].read_bytes() != base[name].read_bytes():
                    raise ValueError(f"Non-product dependency differs: {arm}/{name}")
    return {a: [{"file": name, "bytes": p.stat().st_size} for name, p in sorted(files.items())]
            for a, files in inventories.items()}


def verify_preflight(data, metadata):
    server = next(m["mvid"] for m in metadata["modules"] if m["name"] == "SharpLink.Server")
    if data["serverMvid"] != server or data["identity"] != metadata["identity"]:
        raise ValueError("Preflight used a different server/ABI")
    rows = data["rows"]
    if [(r["transport"], r["declaredTimed"]) for r in rows] != [(t, timed) for t in ["tcp", "sharedmemory"] for timed in [True, False]]:
        raise ValueError("Incomplete blocking preflight inventory")
    for row in rows:
        if row["declaredTimed"]:
            valid = (row["found"] and row["activeRequestId"] == row["stateRequestId"]
                     and row["stateTokenMatchesService"] and row["stateTokenMatchesContext"]
                     and not row["contextMatchesConnection"])
        else:
            valid = not row["found"] and row["contextMatchesConnection"] and not row["serviceCanBeCanceled"]
        if not valid:
            raise ValueError("Request-id/state/token association preflight failed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ["pre-m", "m0", "m1", "build-dotnet", "runtime-dotnet", "output"]:
        parser.add_argument("--" + name, required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    tools = Path(__file__).resolve().parents[2]
    source = Path(__file__).resolve().parent
    roots = {"O": args.pre_m.resolve(), "A": args.m0.resolve(), "B": args.m1.resolve()}
    build = args.build_dotnet.resolve(strict=True)
    runtime = args.runtime_dotnet.resolve(strict=True)
    events = output / "preparation-events.jsonl"

    def run(command, name, cwd=tools, env=None, timeout=None):
        command = list(map(str, command))
        record = {"command": command, "cwd": str(cwd), "startedUtc": utc(), "log": name}
        try:
            with (output / name).open("x") as log:
                result = subprocess.run(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
            record["exitCode"] = result.returncode
            if result.returncode:
                raise subprocess.CalledProcessError(result.returncode, command)
        except Exception as error:
            record.setdefault("exitCode", None)
            record["failure"] = str(error)
            raise
        finally:
            record["endedUtc"] = utc()
            with events.open("a") as log:
                log.write(json.dumps(record) + "\n")

    try:
        write(output / "requested-plan.json", {"profile": CONTROLLED, "sourceRevisions": REVISIONS,
                                               "sdk": SDK, "runtime": RUNTIME, "pairs": PROFILES[CONTROLLED]["pairs"],
                                               "startedUtc": utc(), "noTimingInPreparation": True})
        if platform.system() != "Linux" or platform.machine() not in ["x86_64", "amd64"]:
            raise ValueError("This suite requires the reviewed disposable Linux x64 runner; no local fallback")
        sources = {a: verify_source(root, REVISIONS[a]) for a, root in roots.items()}
        tool_sha = capture(["git", "rev-parse", "HEAD"], tools)
        verify_source(tools, tool_sha)
        for name, cwd in [("tools", tools), *roots.items()]:
            actual = capture([build, "--version"], cwd)
            if actual != SDK:
                raise ValueError(f"Wrong resolved SDK for {name}: {actual}")
        runtimes = capture([runtime, "--list-runtimes"])
        validate_runtime_listing(runtime, runtimes, capture([runtime, "--list-sdks"]))
        overrides = {k: v for k, v in os.environ.items() if re.match(r"(?i)(COMPlus_|DOTNET_(GC|Tiered|TC_|Jit|ReadyToRun|PROCESSOR_COUNT))", k)}
        if overrides:
            raise ValueError(f"No performance environment overrides allowed: {sorted(overrides)}")
        topology = topology_snapshot()
        write(output / "cpu-topology.json", topology)
        taskset = shutil.which("taskset")
        if taskset is None:
            raise ValueError("taskset is required; no unpinned fallback")
        affinity = ",".join(map(str, topology["selectedCpus"]))
        environment = dict(os.environ, DOTNET_ROOT=str(runtime.parent), DOTNET_MULTILEVEL_LOOKUP="0")
        for name, command in [("build-dotnet-info.txt", [build, "--info"]), ("runtime-dotnet-info.txt", [runtime, "--info"]),
                              ("uname.txt", ["uname", "-a"]), ("lscpu.txt", ["lscpu"]), ("taskset.txt", [taskset, "-pc", str(os.getpid())])]:
            run(command, name)
        system_files = [Path("/proc/cpuinfo"), Path("/proc/meminfo"), Path("/sys/fs/cgroup/cpu.max"),
                        Path("/sys/fs/cgroup/cpuset.cpus.effective"), Path("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor")]
        write(output / "system-constraints.json", {str(p): p.read_text() if p.exists() else "unavailable" for p in system_files})
        common = output / "common-source"
        common.mkdir()
        for folder in [source, tools / "eng/method-facts-preflight"]:
            target = common / folder.name
            target.mkdir()
            for p in folder.iterdir():
                if p.is_file() and p.suffix in [".cs", ".csproj", ".py", ".md"]:
                    shutil.copy2(p, target / p.name)
        apps, preflights, build_commands = {}, {}, []
        build_env = dict(os.environ, DOTNET_ROOT=str(build.parent), DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER="1")
        for arm, root in roots.items():
            for kind, folder, project in [("app", source, "CausalProbe.csproj"),
                                          ("preflight", tools / "eng/method-facts-preflight", "Preflight.csproj")]:
                staged = output / "build" / arm / kind
                staged.mkdir(parents=True)
                for p in folder.iterdir():
                    if p.is_file() and p.suffix in [".cs", ".csproj"]:
                        shutil.copy2(p, staged / p.name)
                destination = output / "snapshots" / arm / kind
                command = [build, "publish", staged / project, "-c", "Release", "-m:1", "-nr:false",
                           "-p:UseSharedCompilation=false", f"-p:EvidenceSourceRoot={root}", "-o", destination,
                           f"-bl:{output / (arm + '-' + kind + '.binlog')}"]
                build_commands.append(list(map(str, command)))
                run(command, arm + "-" + kind + "-build.log", cwd=root, env=build_env)
                configs = list(destination.glob("*.runtimeconfig.json"))
                if len(configs) != 1:
                    raise ValueError(f"Expected one runtimeconfig in {destination}")
                pin_runtimeconfig(configs[0])
                (apps if kind == "app" else preflights)[arm] = destination
            verify_source(root, REVISIONS[arm])
        for arm in roots:
            for dll in apps[arm].glob("*.dll"):
                if dll.name != "CausalProbe.dll" and dll.read_bytes() != (preflights[arm] / dll.name).read_bytes():
                    raise ValueError(f"Preflight dependency differs from measured snapshot: {arm}/{dll.name}")
        dependencies = verify_dependencies(apps)
        write(output / "dependency-inventories.json", dependencies)
        run([build, "build-server", "shutdown"], "build-server-shutdown.log", env=build_env)
        write(output / "all-builds-complete.json", {"utc": utc(), "policy": "No build/publish after this marker"})
        metadata = {}
        runtime_contract = {"privateRoot": str(runtime.parent), "version": RUNTIME, "selectedCpus": topology["selectedCpus"]}
        for arm in roots:
            prefix = [taskset, "-c", affinity, runtime]
            run(prefix + [apps[arm] / "CausalProbe.dll", "describe"], arm + "-describe.json", env=environment)
            metadata[arm] = json.loads((output / (arm + "-describe.json")).read_text())["metadata"]
            validate_metadata(metadata[arm], {"runtimeContract": runtime_contract})
            if metadata[arm]["identity"] != ABI[arm]:
                raise ValueError(f"Wrong generated ABI in {arm}")
            run(prefix + [preflights[arm] / "Preflight.dll"], arm + "-preflight.log", env=environment, timeout=180)
            # Preflight diagnostic messages use stderr; the retained combined log ends in its JSON object.
            content = (output / (arm + "-preflight.log")).read_text()
            data = json.loads(content[content.index('{'):])
            write(output / (arm + "-preflight.json"), data)
            verify_preflight(data, metadata[arm])
        pairs = PROFILES[CONTROLLED]["pairs"]
        processes = []
        for index, arm in enumerate("".join(pairs)):
            name = f"process-{index:02}-{arm}"
            samples = output / (name + ".samples.jsonl")
            processes.append({"name": name, "arm": arm, "pairIndex": index // 2, "position": index % 2,
                              "samples": str(samples), "cwd": str(tools),
                              "command": [taskset, "-c", affinity, str(runtime), str(apps[arm] / "CausalProbe.dll"), "network", str(samples)]})
        manifest = {"profile": CONTROLLED, "frozenUtc": utc(), "toolRevision": tool_sha, "sources": sources,
                    "harnessOrigin": "e0e0abf2842094b0d51f4269b39ca678485f9fcf; metadata/runner extension only; original service and per-call loop unchanged",
                    "commonSource": str(common), "sdk": SDK, "runtimeListing": runtimes, "runtimeContract": runtime_contract,
                    "executionEnvironment": {"DOTNET_ROOT": str(runtime.parent), "DOTNET_MULTILEVEL_LOOKUP": "0"},
                    "runner": {k: os.environ.get(k) for k in ["RUNNER_OS", "RUNNER_ARCH", "ImageOS", "ImageVersion", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT"]},
                    "cpuTopology": topology, "performanceSettings": "Default tiering/PGO/GC; explicit two distinct reported-core affinity; no fallback",
                    "buildCommands": build_commands, "metadata": metadata, "dependencyInventories": dependencies,
                    "runtimeConfigs": {str(p): json.loads(p.read_text()) for app in apps.values() for p in app.glob("*.runtimeconfig.json")},
                    "pairs": pairs, "cells": CELLS, "warmupPerCell": WARMUP, "samplesPerCell": SAMPLES,
                    "callsPerSample": CALLS, "totalMeasuredCalls": len(processes)*len(CELLS)*SAMPLES*CALLS,
                    "processes": processes, "monitoring": "Separate five-second process/load/cgroup snapshots; no production probes",
                    "failurePolicy": "Stop at first incomplete/failed process, retain all raw evidence; no retries/replacements",
                    "interpretation": "All three contrasts and T/U separate; no release tolerance or equivalence pass threshold"}
        write(output / "manifest.json", manifest)
        write(output / "preparation-complete.json", {"utc": utc(), "builds": 6, "untimedPreflightRequests": 12,
                                                     "plannedProcesses": 36, "plannedMeasuredCalls": 1440000,
                                                     "timedProcessesExecuted": 0})
    except Exception as error:
        write(output / "preparation-failure.json", {"utc": utc(), "type": type(error).__name__, "error": str(error), "timingStarted": False})
        raise


if __name__ == "__main__":
    main()
