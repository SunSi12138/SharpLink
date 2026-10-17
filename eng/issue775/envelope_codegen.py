#!/usr/bin/env python3
"""Collect actual caller Tier1 for a single immutable-envelope construction experiment."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess

from run import BENCHMARK, COMMON_INPUTS, OVERLAY, PROJECT, digest, execute, git, save
from attribution_report import validate_rpc

OLD = "d2cb248b5e4b573d448092f7f152c2f2dd281e30"
PRODUCTION = {"src/SharpLink.Server/ServerRequestEnvelopeReader.cs",
              "src/SharpLink.Server/SharpLinkServer.StaticBinding.cs"}
CASES = (("tcp", "unary", 8, 4), ("sharedmemory", "unary", 8, 4),
         ("tcp", "oneway", 1, 1), ("sharedmemory", "oneway", 1, 1))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--old", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--expected-candidate", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    roots = {"old": args.old.resolve(), "candidate": args.candidate.resolve()}
    expected = {"old": OLD, "candidate": args.expected_candidate}
    out = args.output.resolve()
    if out.exists() and any(out.iterdir()):
        raise ValueError("Refusing to overwrite evidence")
    out.mkdir(parents=True, exist_ok=True)
    for arm, root in roots.items():
        if not re.fullmatch(r"[0-9a-f]{40}", expected[arm]) or git(root, "rev-parse", "HEAD") != expected[arm]:
            raise ValueError("Wrong source SHA")
        if git(root, "status", "--porcelain"):
            raise ValueError("Dirty source")
    changed = set(git(roots["candidate"], "diff", "--name-only", OLD, "HEAD", "--", "src").splitlines())
    if changed != PRODUCTION:
        raise ValueError(f"Unexpected production change: {changed}")
    common = {}
    for relative in COMMON_INPUTS + OVERLAY:
        if (roots["old"] / relative).read_bytes() != (roots["candidate"] / relative).read_bytes():
            raise ValueError(f"Changed workload or budget: {relative}")
        common[relative] = digest(roots["old"] / relative)
    (out / "production.patch").write_text(git(roots["candidate"], "diff", OLD, "HEAD", "--", "src") + "\n")
    env = os.environ.copy()
    if any(key.startswith(("DOTNET_Jit", "COMPlus_Jit")) for key in env):
        raise ValueError("Inherited JIT settings")
    cpus = sorted(os.sched_getaffinity(0))[:2]
    if len(cpus) != 2:
        raise ValueError("Two CPUs required")
    env |= {"SharpLinkStaticBindingPrototype": "true", "SHARPLINK_EXPECTED_MAGIC": "183",
            "DOTNET_PROCESSOR_COUNT": "2", "DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "1"}
    affinity = ["taskset", "-c", ",".join(map(str, cpus))]
    manifest = {"commits": expected, "cpuAffinity": cpus, "commonInputSha256": common,
                "scope": "Diagnostic JIT only; not timing or equivalence evidence.",
                "stage2Authorized": False, "cases": CASES, "assemblies": {}, "sdk": {}}
    failures = []
    for arm, root in roots.items():
        manifest["sdk"][arm] = subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip()
        arm_env = env | {"SHARPLINK_BENCHMARK_SHA": expected[arm]}
        for name, command in (("environment", ["dotnet", "--info"]),
                              ("build", ["dotnet", "build", PROJECT, "-c", "Release", "-v", "minimal"]),
                              ("smoke", ["dotnet", BENCHMARK, "--static-binding-smoke"]),
                              ("shutdown", ["dotnet", "build-server", "shutdown"])):
            if not execute(command, root, out / arm / name, arm_env, 300):
                raise RuntimeError(f"{arm}/{name} failed")
        manifest["assemblies"][arm] = {p.name: digest(p) for p in sorted((root / Path(BENCHMARK).parent).glob("*.dll"))}
        save(out / "manifest.json", manifest)
        for case in CASES:
            transport, shape, concurrency, pool = case
            name = f"{transport}-{shape}-c{concurrency}-p{pool}"
            directory = out / arm / name
            directory.mkdir(parents=True, exist_ok=True)
            jit_env = arm_env | {"DOTNET_JitDisasm": "*SharpLinkServer*:*",
                                 "DOTNET_JitStdOutFile": str(directory / "disassembly.txt")}
            save(directory / "diagnostic-environment.json", {k: v for k, v in jit_env.items()
                 if k.startswith("DOTNET_") or k.startswith("SHARPLINK_") or k == "SharpLinkStaticBindingPrototype"})
            command = affinity + ["dotnet", BENCHMARK, "--static-binding-evidence", *map(str, case),
                                  "10000", "100000", str(directory / "result.json")]
            if not execute(command, root, directory, jit_env, 120):
                failures.append(f"{arm}/{name}: execution failed")
                continue
            try:
                validate_rpc(json.loads((directory / "result.json").read_text()), "B", name)
                text = (directory / "disassembly.txt").read_text()
                method = "DispatchRpcAsync" if shape == "unary" else "DispatchOneWayRpc"
                # Retain raw output including incomplete/interleaved listings. A Tier1 header
                # is a coverage check only; copying removal requires actual caller review.
                headers = [line for line in text.splitlines() if line.startswith("; Assembly listing for method")
                           and method + "(" in line and "(Tier1)" in line]
                if not headers:
                    raise ValueError(f"Missing actual {method} Tier1")
                save(directory / "tier1-coverage.json", {"caller": method, "headers": headers,
                     "copyEliminationConfirmed": False, "note": "Manual code review required; no automatic gain claim."})
            except Exception as error:
                failures.append(f"{arm}/{name}: {error}")
    if len(set(manifest["sdk"].values())) != 1:
        failures.append("SDK mismatch")
    save(out / "manifest.json", manifest)
    save(out / "execution-failures.json", failures)
    if failures:
        raise RuntimeError("; ".join(failures))


if __name__ == "__main__":
    main()
