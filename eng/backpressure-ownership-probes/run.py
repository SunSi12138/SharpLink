#!/usr/bin/env python3
"""Build two real runtime snapshots and retain fresh, bounded probe results."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

HERE = Path(__file__).resolve().parent


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def command(args, cwd, log, *, env=None, timeout=600):
    with log.open("w") as stream:
        stream.write(json.dumps([str(arg) for arg in args]) + "\n")
        stream.flush()
        try:
            result = subprocess.run(args, cwd=cwd, env=env, stdout=stream,
                                    stderr=subprocess.STDOUT, timeout=timeout, check=False)
            return {"exitCode": result.returncode, "timedOut": False}
        except subprocess.TimeoutExpired:
            stream.write("\nPROBE TIMEOUT: partial samples and log retained.\n")
            return {"exitCode": None, "timedOut": True}


def clean_checkout(root):
    changed = subprocess.check_output(["git", "diff", "HEAD", "--name-only"], cwd=root, text=True).strip()
    untracked = subprocess.check_output(
        ["git", "ls-files", "--others", "--exclude-standard", "--", "src", "eng", "global.json",
         "NuGet.Config", "nuget.config", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"],
        cwd=root, text=True).strip()
    if changed or untracked:
        raise RuntimeError(f"Use committed, clean build inputs in {root}:\n{changed}\n{untracked}")
    return subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path, help="Existing baseline source checkout; never edited or fetched")
    parser.add_argument("candidate", type=Path, help="Existing candidate checkout")
    parser.add_argument("output", type=Path, help="New artifact directory; existing paths are refused")
    parser.add_argument("--smoke", action="store_true", help="Bounded functionality check, not the full cost matrix")
    parser.add_argument("--no-restore", action="store_true", help="Runtime projects already restored")
    args = parser.parse_args()
    roots = {"baseline": args.baseline.resolve(), "candidate": args.candidate.resolve()}
    output = args.output.resolve()
    if output.exists():
        parser.error("Output already exists; use a new path to preserve prior/negative evidence")
    heads = {side: clean_checkout(root) for side, root in roots.items()}
    versions = {side: subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip()
                for side, root in roots.items()}
    if len(set(versions.values())) != 1:
        parser.error(f"Same-SDK comparison required: {versions}")
    output.mkdir(parents=True)
    harness_sources = {p.name: digest(p) for p in HERE.iterdir() if p.is_file()}
    (output / "harness-sources.json").write_text(json.dumps(harness_sources, indent=2) + "\n")
    statuses = {}
    required_failed = False
    common = ["-c", "Release", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false"]
    clean_env = {key: value for key, value in os.environ.items()
                 if not key.startswith(("DOTNET_Tier", "DOTNET_TC_", "DOTNET_Jit", "COMPlus_"))}
    for side, root in roots.items():
        target = output / side
        target.mkdir()
        provenance = {"sdk": versions[side], "checkout": str(root), "smoke": args.smoke,
                      "head": heads[side],
                      "status": subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True),
                      "sources": {}}
        for name in ("RpcSession.cs", "RpcSession.SendPump.cs", "RpcSession.SendPump.BackpressureOwnership.cs"):
            path = root / "src/SharpLink.Runtime" / name
            provenance["sources"][name] = digest(path) if path.exists() else None
        (target / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
        build = ["dotnet", "build", str(root / "src/SharpLink.Runtime/SharpLink.Runtime.csproj"), *common]
        if args.no_restore:
            build.append("--no-restore")
        result = command(build, root, target / "runtime-build.log")
        if result["exitCode"] != 0:
            raise RuntimeError(f"{side} runtime build failed; see {target}")
        runtime_dir = root / "src/SharpLink.Runtime/bin/Release/net10.0"
        result = command(["dotnet", "build", str(HERE / "BackpressureOwnershipProbes.csproj"), *common,
                          "--artifacts-path", str(target / "build"), "--output", str(target / "bin"),
                          f"-p:RuntimeDir={runtime_dir}"], roots["candidate"], target / "probe-build.log")
        if result["exitCode"] != 0:
            raise RuntimeError(f"{side} probe build failed; see {target}")
        dll = target / "bin/SharpLink.Benchmarks.dll"
        provenance["probeAssemblySha256"] = digest(dll)
        provenance["runtimeAssemblySha256"] = digest(target / "bin/SharpLink.Runtime.dll")
        (target / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
        statuses[side] = {}

        def probe(name, mode, parameters=(), settings=None, timeout=180):
            env = clean_env | (settings or {})
            result = command(["dotnet", str(dll), mode, str(target / f"{name}.json"), *map(str, parameters)],
                             roots["candidate"], target / f"{name}.log", env=env, timeout=timeout)
            statuses[side][name] = result
            (output / "status.json").write_text(json.dumps(statuses, indent=2) + "\n")
            return result["exitCode"] == 0

        for mode in ("structure", "witness", "late-recovery", "held-cleanup"):
            required_failed |= not probe(mode, mode, settings={"DOTNET_TieredCompilation": "0"})
        profiles = {
            "FullOpts": ({"DOTNET_TieredCompilation": "0"}, 3000, 10000),
            "Tier0": ({"DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "0",
                       "DOTNET_TC_QuickJit": "1", "DOTNET_TC_QuickJitForLoops": "1",
                       "DOTNET_TC_CallCountThreshold": "7FFFFFFF"}, 50, 50),
            "Tier1NoPGO": ({"DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "0",
                           "DOTNET_TC_CallCountingDelayMs": "0", "DOTNET_TC_CallCountThreshold": "30"}, 5000, 50000),
            "DefaultTiered": ({}, 10000, 500000),
        }
        if args.smoke:
            profiles = {"FullOpts": ({"DOTNET_TieredCompilation": "0"}, 20, 100)}
        for profile, (settings, warmup, operations) in profiles.items():
            settings |= {"DOTNET_JitDisasm": "SendPacketWithBackpressureAsync TryEnqueueForBackpressure TryEnqueue TryReserve PulseCapacityWaiters CompleteReserved ReleaseBatch MoveNext",
                         "DOTNET_JitStdOutFile": str(target / f"jit-{profile}.asm")}
            required_failed |= not probe(f"fast-{profile}", "fast", (warmup, operations), settings, timeout=600)
        for contenders, cancellable, cancelled in ((1, False, False), (1, True, False), (16, False, False),
                                                   (16, True, False), (1, True, True), (16, True, True)):
            name = f"saturated-{contenders}-token{int(cancellable)}-cancel{int(cancelled)}"
            passed = probe(name, "saturated", (2 if args.smoke else 100, 3 if args.smoke else 500,
                           contenders, str(cancellable).lower(), str(cancelled).lower()),
                           {"DOTNET_TieredCompilation": "0"}, timeout=180)
            # A known baseline accepted-16 stall is retained, never a successful sample.
            known_baseline_stall = (side == "baseline" and contenders == 16 and not cancelled
                                    and (target / f"{name}.json.partial").exists())
            if not known_baseline_stall:
                required_failed |= not passed
    result = command([sys.executable, str(HERE / "compare.py"), str(output)], roots["candidate"], output / "compare.log")
    required_failed |= result["exitCode"] != 0
    final_heads = {side: clean_checkout(root) for side, root in roots.items()}
    if final_heads != heads:
        (output / "CHECKOUT-CHANGED.json").write_text(json.dumps(final_heads, indent=2) + "\n")
        required_failed = True
    current_sources = {p.name: digest(p) for p in HERE.iterdir() if p.is_file()}
    if harness_sources != current_sources:
        (output / "HARNESS-CHANGED.json").write_text(json.dumps(current_sources, indent=2) + "\n")
        required_failed = True
    entries = [f"{digest(p)}  {p.relative_to(output)}" for p in sorted(output.rglob("*")) if p.is_file()]
    (output / "MANIFEST.sha256").write_text("\n".join(entries) + "\n")
    print(f"Evidence: {output}; required probes {'FAILED' if required_failed else 'completed'}. Read status.json, not exit alone.")
    return 1 if required_failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
