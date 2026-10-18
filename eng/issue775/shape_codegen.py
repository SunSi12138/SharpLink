#!/usr/bin/env python3
"""Fixed-source method-shape codegen experiment. Never a throughput score."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess

from boundary_codegen import CASES, overlay, validate_result
from run import BENCHMARK, COMMON_INPUTS, OVERLAY, PROJECT, digest, execute, git, save

OLD = "8810547756d1c16367bba8d81f6707018b9d7e5d"
PRODUCTION = "src/SharpLink.Server/StaticBindingTable.cs"
FILTER = "*StaticBindingTable:Resolve *SharpLinkServer:ResolveStaticRequest *SharpLinkServer:DispatchRpcAsync *SharpLinkServer:DispatchOneWayRpc"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("old", "candidate", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--expected-candidate", required=True)
    args = parser.parse_args()
    roots = {"old": args.old.resolve(), "candidate": args.candidate.resolve()}
    commits = {"old": OLD, "candidate": args.expected_candidate}
    out = args.output.resolve()
    if out.exists() and any(out.iterdir()):
        raise ValueError("Do not overwrite evidence")
    out.mkdir(parents=True, exist_ok=True)
    for arm, root in roots.items():
        if not re.fullmatch(r"[0-9a-f]{40}", commits[arm]) or git(root, "rev-parse", "HEAD") != commits[arm] or git(root, "status", "--porcelain"):
            raise ValueError("Wrong or dirty source")
    if git(roots["candidate"], "diff", "--name-only", OLD, "HEAD", "--", "src") != PRODUCTION:
        raise ValueError("Production changes are not restricted to StaticBindingTable")
    common = {}
    for relative in COMMON_INPUTS + OVERLAY + ("eng/issue775/BoundaryDiagnostics.cs",):
        if (roots["old"] / relative).read_bytes() != (roots["candidate"] / relative).read_bytes():
            raise ValueError(f"Changed diagnostic, workload or policy: {relative}")
        common[relative] = digest(roots["old"] / relative)
    (out / "production.patch").write_text(git(roots["candidate"], "diff", OLD, "HEAD", "--", "src") + "\n")
    canonical = os.environ.copy()
    if any(k.startswith(("DOTNET_", "COMPlus_", "CORECLR_", "COR_")) and
           any(x in k.lower() for x in ("jit", "tier", "processor_count", "profiler")) for k in canonical):
        raise ValueError("Inherited diagnostic settings")
    cpus = sorted(os.sched_getaffinity(0))[:2]
    if len(cpus) != 2:
        raise ValueError("Two CPUs required")
    env = canonical | {"SharpLinkStaticBindingPrototype": "true", "SHARPLINK_EXPECTED_MAGIC": "183",
        "DOTNET_PROCESSOR_COUNT": "2", "DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "1"}
    manifest = {"commits": commits, "cpuAffinity": cpus, "commonInputSha256": common,
        "jitFilter": FILTER, "sdk": {}, "assemblies": {}, "overlay": {},
        "stage2Authorized": False, "performanceEvidence": False}
    failures, runtimes = [], set()
    for arm, root in roots.items():
        manifest["sdk"][arm] = subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip()
        manifest["overlay"][arm] = overlay(root, roots["candidate"], out / arm / "overlay")
        arm_env = env | {"SHARPLINK_BENCHMARK_SHA": commits[arm]}
        save(out / "manifest.json", manifest)
        for name, command in (("environment", ["dotnet", "--info"]), ("cpu", ["lscpu"]),
            ("build", ["dotnet", "build", PROJECT, "-c", "Release", "-v", "minimal"]),
            ("smoke", ["dotnet", BENCHMARK, "--static-binding-smoke"]),
            ("shutdown", ["dotnet", "build-server", "shutdown"])):
            if not execute(command, root, out / arm / name, arm_env, 300):
                raise RuntimeError(f"{arm}/{name} failed")
        manifest["assemblies"][arm] = {p.name: digest(p) for p in sorted((root / Path(BENCHMARK).parent).glob("*.dll"))}
        for transport, shape in CASES:
            directory = out / arm / f"{transport}-{shape}"
            directory.mkdir(parents=True, exist_ok=True)
            jit = arm_env | {"DOTNET_JitDisasm": FILTER, "DOTNET_JitStdOutFile": str(directory / "disassembly.txt")}
            save(directory / "diagnostic-environment.json", {k:v for k,v in jit.items() if k.startswith("DOTNET_")})
            argv = ["taskset", "-c", ",".join(map(str, cpus)), "dotnet", BENCHMARK,
                    "--boundary-codegen", transport, shape, str(directory / "result.json")]
            if not execute(argv, root, directory, jit, 180):
                failures.append(f"{arm}/{transport}-{shape}: execution")
                continue
            try:
                value = json.loads((directory / "result.json").read_text())
                validate_result(value, transport, shape)
                runtimes.add(value["Runtime"])
                caller = "DispatchRpcAsync" if shape == "unary" else "DispatchOneWayRpc"
                headers = [line for line in (directory / "disassembly.txt").read_text().splitlines()
                    if line.startswith("; Assembly listing for method") and caller + "(" in line and "(Tier1)" in line]
                if not headers:
                    raise ValueError("Missing actual caller Tier1")
                save(directory / "coverage.json", {"headers": headers, "mechanismConfirmed": False,
                    "note": "Manual review of complete actual caller required; not a speedup claim."})
            except Exception as error:
                failures.append(f"{arm}/{transport}-{shape}: {error}")
    if len(set(manifest["sdk"].values())) != 1 or len(runtimes) != 1:
        failures.append("SDK/runtime mismatch")
    save(out / "manifest.json", manifest)
    save(out / "execution-failures.json", failures)
    if failures:
        raise RuntimeError("; ".join(failures))


if __name__ == "__main__":
    main()
