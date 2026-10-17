#!/usr/bin/env python3
"""Fixed-source diagnostic comparisons. Never grants performance or phase-2 approval."""
import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess

from run import BENCHMARK, COMMON_INPUTS, OVERLAY, PROJECT, digest, execute, git, save
from attribution_report import report, validate_rpc

BASE = "1a54993d3550911f5979a7b4d3f9f5f63ee86a64"
CANDIDATE = "d2cb248b5e4b573d448092f7f152c2f2dd281e30"
COMMITS = {"A": BASE, "B": CANDIDATE, "C": CANDIDATE}
# Each arm occupies each position twice; every permutation is retained.
BLOCKS = ("ABC", "BCA", "CAB", "CBA", "ACB", "BAC")
AA_ORDER = "XYYXYXXY"  # X and Y both execute the SAME A directory and binaries.
CASES = tuple((t, s, c, p) for t in ("tcp", "sharedmemory")
              for s in ("unary", "oneway")
              for c, p in (((1, 1), (1, 4), (8, 1), (8, 4)) if s == "unary" else ((1, 1), (8, 4))))
FOCUS = (("tcp", "unary", 8, 4), ("tcp", "oneway", 1, 1))
WARMUP, OPERATIONS = 10000, 100000
TRACE_VERSION = "9.0.661903"
PROBE = "eng/issue775/attribution-probe/AttributionProbe.csproj"
PROBE_DLL = "eng/issue775/attribution-probe/bin/Release/net10.0/AttributionProbe.dll"
PROVIDERS = ("Microsoft-DotNETCore-SampleProfiler:0xF00000000000:4,"
             "Microsoft-Windows-DotNETRuntime:0x1000034019:4,"
             "System.Runtime:0:4:EventCounterIntervalSec=1,SharpLink-Issue775-Attribution:0:4")


def case_name(case):
    t, s, c, p = case
    return f"{t}-{s}-c{c}-p{p}"


def arm_env(arm, initial):
    return initial | {"SharpLinkStaticBindingPrototype": "true" if arm == "B" else "false",
                      "SHARPLINK_BENCHMARK_SHA": COMMITS[arm],
                      "SHARPLINK_EXPECTED_MAGIC": "183" if arm == "B" else "137"}


def output_hashes(root):
    return {str(p.relative_to(root)): digest(p) for p in sorted((root / Path(BENCHMARK).parent).glob("*.dll"))}


def prepare(args):
    driver = args.driver.resolve()
    out = args.output.resolve()
    if out.exists() and any(out.iterdir()):
        raise ValueError("Output must be empty: no merging or overwriting earlier experiments")
    out.mkdir(parents=True, exist_ok=True)
    roots = {arm: (args.work / arm).resolve() for arm in COMMITS}
    manifest = {"schema": 1, "mode": args.mode, "commits": COMMITS,
                "driverSha": git(driver, "rev-parse", "HEAD"), "host": platform.node(),
                "platform": platform.platform(), "blocks": BLOCKS, "aaOrder": AA_ORDER,
                "cases": [case_name(c) for c in CASES], "focus": [case_name(c) for c in FOCUS],
                "warmup": WARMUP, "operations": OPERATIONS, "stage2Authorized": False,
                "aaPlacement": "XYYX before ABC matrix; YXXY after; never retried",
                "traceVersion": TRACE_VERSION, "commonInputs": {}, "overlay": {}, "assemblies": {}}
    for arm, root in roots.items():
        if git(root, "rev-parse", "HEAD") != COMMITS[arm] or git(root, "status", "--porcelain"):
            raise ValueError(f"{arm}: wrong or dirty checkout")
    for rel in COMMON_INPUTS:
        hashes = {digest(root / rel) for root in roots.values()}
        if len(hashes) != 1:
            raise ValueError(f"Changed workload, dependencies or policy: {rel}")
        manifest["commonInputs"][rel] = hashes.pop()
    for rel in OVERLAY:
        data = (roots["B"] / rel).read_bytes()
        if (roots["C"] / rel).read_bytes() != data:
            raise ValueError(f"B/C harness mismatch: {rel}")
        (roots["A"] / rel).write_bytes(data)
        dest = out / "overlay" / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(data)
        manifest["overlay"][rel] = digest(dest)
    (out / "baseline-overlay.patch").write_text(git(roots["A"], "diff") + "\n")
    if git(roots["A"], "diff", "--", "src", "Directory.Build.props", "eng/perf"):
        raise ValueError("Production or allocation policy overlay is forbidden")
    canonical = os.environ.copy()
    bad = [k for k in canonical if k.startswith(("DOTNET_", "COMPlus_", "CORECLR_", "COR_"))
           and any(x in k.lower() for x in ("jit", "tier", "profiler", "diagnosticport", "processor_count"))]
    if bad:
        raise ValueError(f"Undeclared diagnostic/timing environment: {bad}")
    cpus = sorted(os.sched_getaffinity(0))[:2]
    if len(cpus) != 2:
        raise ValueError("Two available CPUs required")
    timing = canonical | {"DOTNET_PROCESSOR_COUNT": "2", "DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "1"}
    affinity = ["taskset", "-c", ",".join(map(str, cpus))]
    versions = {a: subprocess.check_output(["dotnet", "--version"], cwd=r, text=True).strip() for a, r in roots.items()}
    if len(set(versions.values())) != 1:
        raise ValueError("SDK mismatch")
    manifest.update(cpuAffinity=cpus, sdkVersions=versions, dotnetProcessorCount=2,
                    tieredCompilation=1, tieredPgo=1, canonicalAffinity=sorted(os.sched_getaffinity(0)))
    save(out / "manifest.json", manifest)
    for name, command in (("dotnet", ["dotnet", "--info"]), ("cpu", ["lscpu"])):
        if not execute(command, driver, out / "environment" / name, canonical):
            raise RuntimeError(f"Environment capture failed: {name}")
    for arm, root in roots.items():
        env = arm_env(arm, timing)
        for name, command in (("restore", ["dotnet", "restore", PROJECT]),
                              ("build", ["dotnet", "build", PROJECT, "-c", "Release", "--no-restore", "-v", "minimal"])):
            if not execute(command, root, out / "build" / arm / name, env, 300):
                raise RuntimeError(f"{arm} {name} failed; refusing stale output")
        manifest["assemblies"][arm] = output_hashes(root)
    save(out / "manifest.json", manifest)
    if not execute(["dotnet", BENCHMARK, "--static-binding-smoke"], roots["B"], out / "smoke", arm_env("B", canonical), 120):
        raise RuntimeError("Safety smoke failed; diagnostics blocked")
    # Stop build servers before either measurement or profiling, in ALL arms.
    if not execute(["dotnet", "build-server", "shutdown"], driver, out / "build-server-shutdown", canonical, 60):
        raise RuntimeError("Could not stop build servers")
    return driver, out, roots, manifest, canonical, timing, affinity


def bdn(root, slot, env, affinity):
    return execute(affinity + ["dotnet", "run", "--project", PROJECT, "-c", "Release", "--no-build", "--no-restore", "--",
                   "--filter", "*UnaryBenchmarks.Rpc_OnewayPublish*", "--launchCount", "3", "--warmupCount", "6",
                   "--iterationCount", "15", "--invocationCount", "4096", "--exporters", "json", "--artifacts", str(slot / "bdn"),
                   "--keepFiles", "--buildTimeout", "180"], root, slot / "bdn-command", env, 300)


def rpc_argv(case, dest, count=OPERATIONS):
    return ["dotnet", BENCHMARK, "--static-binding-evidence", *map(str, case), str(WARMUP), str(count), str(dest)]


def timing_run(context):
    driver, out, roots, manifest, canonical, timing, affinity = context
    failures, executions = [], []
    def slot_run(relative, arm, cases, group, block, label):
        slot = out / relative
        env = arm_env(arm, timing)
        executions.append({"path": relative, "arm": arm, "label": label, "group": group, "block": block,
                           "cases": [case_name(c) for c in cases], "assemblySha256": manifest["assemblies"][arm]})
        save(out / "executions.json", executions)
        if not bdn(roots[arm], slot, env, affinity):
            failures.append(relative + "/bdn")
        for case in cases:
            directory = slot / "rpc" / case_name(case)
            if not execute(affinity + rpc_argv(case, directory / "result.json"), roots[arm], directory, env, 120):
                failures.append(str(directory.relative_to(out)))
        save(out / "execution-failures.json", failures)

    for i, label in enumerate(AA_ORDER[:4], 1):
        slot_run(f"aa/{i:02d}-{label}", "A", FOCUS, "aa", (i-1)//2, label)
    for block, order in enumerate(BLOCKS, 1):
        for position, arm in enumerate(order, 1):
            slot_run(f"matrix/{block:02d}/{position}-{arm}", arm, CASES, "matrix", block, arm)
    for i, label in enumerate(AA_ORDER[4:], 5):
        slot_run(f"aa/{i:02d}-{label}", "A", FOCUS, "aa", (i-1)//2, label)
    # Unchanged canonical budgets, once per compiled arm; diagnosis is not a replacement for full acceptance.
    for arm, root in roots.items():
        gate_env = arm_env(arm, canonical) | {"SHARPLINK_ALLOCATION_OUTPUT": str(out / "allocation" / arm / "result.json"),
            "SHARPLINK_ALLOCATION_BUDGETS": str(root / "eng/perf/allocation-budgets.json")}
        if not execute(["bash", "eng/run-allocation-gate.sh"], root, out / "allocation" / arm, gate_env, 180):
            failures.append("allocation/" + arm)
    env = arm_env("B", canonical) | {"SHARPLINK_ALLOCATION_OUTPUT": str(out / "negative-control/result.json"),
        "SHARPLINK_ALLOCATION_BUDGETS": str(roots["B"] / "eng/perf/allocation-budgets.json")}
    if not execute(["bash", "eng/run-allocation-gate.sh", "--filter", "rpc-add-sharedmemory-c1",
                    "--inject-bytes-per-operation", "512"], roots["B"], out / "negative-control", env, 120, expected_failure=True):
        failures.append("negative-control")
    for arm, root in roots.items():
        if output_hashes(root) != manifest["assemblies"][arm]:
            failures.append(f"{arm}: binaries changed during uninstrumented experiment")
    save(out / "execution-failures.json", failures)
    try:
        report(out)
    except Exception as error:
        failures.append(f"Validation: {error}")
    save(out / "execution-failures.json", failures)
    if failures:
        raise RuntimeError("; ".join(failures))


def profiles_run(context):
    driver, out, roots, manifest, canonical, timing, affinity = context
    tools = out / "tools"
    failures = []
    if not execute(["dotnet", "tool", "install", "dotnet-trace", "--version", TRACE_VERSION,
                    "--tool-path", str(tools)], driver, out / "trace-install", canonical, 180):
        raise RuntimeError("Pinned trace tool install failed; no fallback version")
    trace = str(tools / "dotnet-trace")
    tool_env = canonical | {"DOTNET_ROLL_FORWARD": "Major"}
    for verb in ("--version", "list-profiles"):
        if not execute([trace, verb], driver, out / ("trace-" + verb.lstrip("-")), tool_env):
            raise RuntimeError("Trace tool verification failed")
    for arm, root in roots.items():
        env = arm_env(arm, timing)
        for name in ("AttributionProbe.csproj", "Program.cs"):
            dest = root / "eng/issue775/attribution-probe" / name
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(driver / "eng/issue775/attribution-probe" / name, dest)
        if not execute(["dotnet", "build", PROBE, "-c", "Release", "-v", "minimal"], root, out / "probe-build" / arm, env, 300):
            failures.append(f"{arm}: probe build")
            continue
        if not execute(["dotnet", "build-server", "shutdown"], root, out / "probe-shutdown" / arm, canonical, 60):
            raise RuntimeError("Build server shutdown failed")
        # These are separate, deliberately instrumented runs. Their timing NEVER enters report().
        for name, case in (("tcp-unary-c8-p4", ("tcp", "unary", 8, 4)),
                           ("sharedmemory-unary-c8-p4", ("sharedmemory", "unary", 8, 4)),
                           ("original-oneway-expression", None)):
            directory = out / "profiles" / arm / name
            directory.mkdir(parents=True, exist_ok=True)
            report_path = directory / "workload.json"
            argv = (["dotnet", PROBE_DLL, str(report_path)] if case is None else rpc_argv(case, report_path, 300000))
            traced_env = env | {"DOTNET_ROLL_FORWARD": "Major"}
            command = affinity + [trace, "collect", "--providers", PROVIDERS, "--buffersize", "256",
                                  "--output", str(directory / "trace.nettrace"), "--show-child-io", "--"] + argv
            if not execute(command, root, directory / "collect", traced_env, 180):
                failures.append(f"{arm}/{name}: collect")
                continue
            try:
                data = json.loads(report_path.read_text())
                if case is None:
                    if data["Completed"] != 1000000 or data.get("BatchSize") != 4096 or data["Magic"] != int(env["SHARPLINK_EXPECTED_MAGIC"]):
                        raise ValueError("Probe count/dialect mismatch")
                else:
                    validate_rpc(data, arm, case_name(case), 300000)
            except Exception as error:
                failures.append(f"{arm}/{name}: child {error}")
            tracefile = directory / "trace.nettrace"
            for inclusive in (False, True):
                command = [trace, "report", str(tracefile), "topN", "-n", "80", "--verbose"]
                if inclusive:
                    command.append("--inclusive")
                if not execute(command, root, directory / ("inclusive" if inclusive else "exclusive"), tool_env, 120):
                    failures.append(f"{arm}/{name}: topN")
            if not execute([trace, "convert", str(tracefile), "--format", "Speedscope", "--output", str(directory / "stacks")],
                           root, directory / "convert", tool_env, 120):
                failures.append(f"{arm}/{name}: convert")
            # JIT output is a different process again, without EventPipe collection.
            jit = out / "jit" / arm / name
            jit.mkdir(parents=True, exist_ok=True)
            env_jit = env | {"DOTNET_JitDisasm": "*SharpLinkClient*:* *SharpLinkServer*:* *StaticBindingTable*:*",
                             "DOTNET_JitStdOutFile": str(jit / "disassembly.txt")}
            argv = (["dotnet", PROBE_DLL, str(jit / "workload.json")] if case is None
                    else rpc_argv(case, jit / "workload.json", 100000))
            save(jit / "diagnostic-environment.json", {k: v for k, v in env_jit.items() if k.startswith("DOTNET_")})
            if not execute(affinity + argv, root, jit, env_jit, 120):
                failures.append(f"{arm}/{name}: JIT")
            if not (jit / "disassembly.txt").exists() or (jit / "disassembly.txt").stat().st_size == 0:
                failures.append(f"{arm}/{name}: missing disassembly")
    save(out / "execution-failures.json", failures)
    save(out / "diagnostic-notes.json", {"stage2Authorized": False, "providers": PROVIDERS,
        "interpretation": "Thread-stack wall-time sampling, NOT on-CPU attribution. Raw runtime events retained in nettrace.",
        "scope": "RPC traces include startup, Prepare, warmup, measurement and JSON. Sender probe marks phases and 4096-call batches separated by existing validated Rpc_Add; not BDN timing or OneWay receipt.",
        "jit": "Default tiering/PGO retained. Release-supported class-qualified method filter; no Debug/Checked-only assembly filter. Separate diagnostic processes; all available tiers retained.",
        "failureCount": len(failures)})
    if failures:
        raise RuntimeError("; ".join(failures))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--driver", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--mode", choices=("timing", "profiles"), required=True)
    args = parser.parse_args()
    (timing_run if args.mode == "timing" else profiles_run)(prepare(args))


if __name__ == "__main__":
    main()
