#!/usr/bin/env python3
"""Fixed-source sender CPU / queue diagnostics, separated from formal performance runs."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
from run import BENCHMARK, PROJECT, COMMON_INPUTS, OVERLAY, execute, git, digest, save
from client_send_report import COMMITS, ORDER, CASES, report, validate


def overlay(root, driver, arm, out):
    changes = {}
    if arm == "A":
        for rel in OVERLAY:
            changes[rel] = (driver/rel).read_bytes()
    additions = {
        "src/SharpLink.Client/Issue775ClientProbe.cs": "ClientProbe.cs",
        "test/SharpLink.Benchmarks/Issue775SendDiagnostics.cs": "SendDiagnostics.cs"}
    for dest, name in additions.items():
        if (root/dest).exists():
            raise ValueError("Unexpected preexisting diagnostic file")
        changes[dest] = (driver/"eng/issue775/client-send"/name).read_bytes()
    program = "test/SharpLink.Benchmarks/Program.cs"
    text = changes.get(program, (root/program).read_bytes()).decode()
    marker = "    public static async Task Main(string[] args)\n    {\n"
    if text.count(marker) != 1:
        raise ValueError("Entrypoint overlay anchor changed")
    text = text.replace(marker, marker + '        if (args.Length > 0 && args[0] == "--client-send-diagnostic")\n'
        '        {\n            await ClientSendDiagnostics.RunAsync(args[1..]);\n            return;\n        }\n')
    changes[program] = text.encode()
    for rel, data in changes.items():
        (root/rel).parent.mkdir(parents=True, exist_ok=True)
        (root/rel).write_bytes(data)
        copy = out/rel
        copy.parent.mkdir(parents=True, exist_ok=True)
        copy.write_bytes(data)
    # New diagnostic shim only; no tracked src file may be edited.
    if git(root, "diff", "--", "src", "Directory.Build.props", "eng/perf"):
        raise ValueError("Diagnostic overlay edited existing production or policy")
    return {rel: digest(root/rel) for rel in changes}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("base", "candidate", "driver", "output"):
        parser.add_argument("--"+name, type=Path, required=True)
    args = parser.parse_args()
    roots = {"A": args.base.resolve(), "B": args.candidate.resolve()}
    driver, out = args.driver.resolve(), args.output.resolve()
    if out.exists() and any(out.iterdir()):
        raise ValueError("Never merge or overwrite runs")
    out.mkdir(parents=True, exist_ok=True)
    for arm, root in roots.items():
        if git(root, "rev-parse", "HEAD") != COMMITS[arm] or git(root, "status", "--porcelain"):
            raise ValueError("Wrong or dirty source")
    common = {}
    for rel in COMMON_INPUTS:
        if (roots["A"]/rel).read_bytes() != (roots["B"]/rel).read_bytes():
            raise ValueError("Workload/policy changed: "+rel)
        common[rel] = digest(roots["B"]/rel)
    manifest = {"commits": COMMITS, "driverSha": git(driver, "rev-parse", "HEAD"),
                "order": ORDER, "commonSha256": common, "overlay": {}, "assemblies": {},
                "diagnosticOnly": True, "stage2Authorized": False, "noPerformanceAcceptance": True}
    cpus = sorted(os.sched_getaffinity(0))[:2]
    if len(cpus) != 2:
        raise ValueError("Two CPUs required")
    manifest["cpus"] = cpus
    initial = os.environ.copy()
    if any(k.startswith(("DOTNET_", "COMPlus_", "CORECLR_", "COR_")) and
           any(s in k.lower() for s in ("jit", "tier", "profiler", "processor_count", "perfmap")) for k in initial):
        raise ValueError("Inherited diagnostic environment")
    common_env = initial | {"DOTNET_PROCESSOR_COUNT": "2", "DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "1"}
    envs = {arm: common_env | {"SharpLinkStaticBindingPrototype": "true" if arm == "B" else "false",
        "SHARPLINK_BENCHMARK_SHA": sha, "SHARPLINK_EXPECTED_MAGIC": "183" if arm == "B" else "137"}
        for arm, sha in COMMITS.items()}
    for arm, root in roots.items():
        manifest["overlay"][arm] = overlay(root, driver, arm, out/"overlay"/arm)
        for name, argv in (("environment", ["dotnet", "--info"]), ("cpu", ["lscpu"]),
            ("build", ["dotnet", "build", PROJECT, "-c", "Release", "-v", "minimal"])):
            if not execute(argv, root, out/"build"/arm/name, envs[arm], 300):
                raise RuntimeError(f"{arm}/{name} failed")
        manifest["assemblies"][arm] = {p.name: digest(p) for p in sorted((root/Path(BENCHMARK).parent).glob("*.dll"))}
    save(out/"manifest.json", manifest)
    if not execute(["dotnet", BENCHMARK, "--static-binding-smoke"], roots["B"], out/"smoke", envs["B"], 120):
        raise RuntimeError("Safety smoke failed")
    if not execute(["dotnet", "build-server", "shutdown"], driver, out/"shutdown", initial, 60):
        raise RuntimeError("Build server shutdown failed")
    affinity = ["taskset", "-c", ",".join(map(str, cpus))]
    failures = []
    def run_case(arm, case, directory, env):
        transport, c, p = case.split("-")
        directory.mkdir(parents=True, exist_ok=True)
        argv = affinity + ["dotnet", BENCHMARK, "--client-send-diagnostic", transport, c[1:], p[1:], str(directory/"result.json")]
        if not execute(argv, roots[arm], directory, env, 210):
            failures.append(str(directory.relative_to(out))+": execution")
            return
        try:
            validate(json.loads((directory/"result.json").read_text()), arm, case)
        except Exception as error:
            failures.append(str(directory.relative_to(out))+": "+str(error))
        save(out/"execution-failures.json", failures)
    for i, arm in enumerate(ORDER, 1):
        for case in CASES:
            run_case(arm, case, out/"slots"/f"{i:02d}-{arm}"/case, envs[arm])
    save(out/"execution-failures.json", failures)
    try:
        report(out)
    except Exception as error:
        failures.append("report: "+str(error))
    # Separate processes with the SAME bounded workload, never included in timing summary.
    for arm in COMMITS:
        for case in CASES:
            directory = out/"jit"/arm/case
            jit = envs[arm] | {"DOTNET_JitDisasm": "*SharpLinkClient*:* *ClientConnection*:* *StaticBindingRpcChannel*:*",
                              "DOTNET_JitStdOutFile": str(directory/"disassembly.txt")}
            save(directory/"jit-environment.json", {k: v for k, v in jit.items() if k.startswith("DOTNET_")})
            run_case(arm, case, directory, jit)
            if (directory/"disassembly.txt").exists():
                headers = [s for s in (directory/"disassembly.txt").read_text(errors="replace").splitlines()
                    if s.startswith("; Assembly listing for method")]
                save(directory/"coverage.json", {"headers": headers, "mechanismConfirmed": False})
            else:
                failures.append(f"{arm}/{case}: JIT listing missing")
    for arm, root in roots.items():
        actual = {p.name: digest(p) for p in sorted((root/Path(BENCHMARK).parent).glob("*.dll"))}
        if actual != manifest["assemblies"][arm]:
            failures.append(arm+": output DLL changed")
    save(out/"execution-failures.json", failures)
    if failures:
        raise RuntimeError("; ".join(failures))


if __name__ == "__main__":
    main()
