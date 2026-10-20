#!/usr/bin/env python3
"""Three predeclared independent-host replications of one frozen bridge candidate."""
import argparse
import importlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys

BASE = "6cfb09e7dd6b68695f92d28f726340bc8eb39e1d"
EXPERIMENT = "5487a21f020e16c2b036b439b8d7efd896267422"
CANDIDATE = "8fc84d486c242d67fe05d4cf9962661d8351c779"
TREE = "1bd11d1d56ef951428440ee5cede21af94d1c80e"
HOSTS = (1, 2, 3)
AA_ORDER = "XYYXYXXY"
FOCUS = (("tcp", "oneway", 8, 4), ("sharedmemory", "oneway", 8, 4),
         ("sharedmemory", "unary", 8, 4))
PRIMARY = ("tcp-oneway-c8-p4/ReceiverOperationsPerSecond",
           "sharedmemory-oneway-c8-p4/ReceiverOperationsPerSecond",
           "sharedmemory-unary-c8-p4/ReceiverOperationsPerSecond",
           "sharedmemory-unary-c8-p4/CpuMilliseconds")


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def case_name(case):
    t, s, c, p = case
    return f"{t}-{s}-c{c}-p{p}"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("candidate", "old", "experiment", "output"):
        parser.add_argument("--" + key, type=Path, required=True)
    parser.add_argument("--replicate", type=int, choices=HOSTS, required=True)
    args = parser.parse_args()
    candidate, old, experiment, out = (getattr(args, k).resolve() for k in ("candidate", "old", "experiment", "output"))
    if out.exists() and any(out.iterdir()):
        raise ValueError("Never overwrite or merge an experiment")
    out.mkdir(parents=True, exist_ok=True)
    driver = Path(__file__).resolve().parents[2]
    if git(experiment, "rev-parse", "HEAD") != EXPERIMENT or git(experiment, "status", "--porcelain"):
        raise ValueError("Wrong or dirty frozen experiment checkout")
    if git(old, "rev-parse", "HEAD") != BASE or git(old, "status", "--porcelain"):
        raise ValueError("Wrong or dirty old-static comparator")
    # All existing builder, workload, validation and paired-run code comes from a pinned commit.
    sys.path.insert(0, str(experiment / "eng/issue775"))
    bridge = importlib.import_module("bridge")
    runner = importlib.import_module("boundary_pair")
    common = importlib.import_module("run")
    audit = importlib.import_module("attribution_report")
    identity = bridge.apply_candidate(candidate, experiment, out)
    if identity["localCandidateSha"] != CANDIDATE or identity["tree"] != TREE:
        raise ValueError("Candidate changed since the discovery run")
    # Candidates must not include last round's detached-site experiment or semantic overlays.
    if git(candidate, "status", "--porcelain"):
        raise ValueError("Unexpected overlay")
    initial = os.environ.copy()
    bad = [k for k in initial if k.startswith(("DOTNET_", "COMPlus_", "CORECLR_", "COR_"))
           and any(x in k.lower() for x in ("jit", "tier", "profiler", "processor_count", "perfmap", "diagnosticport"))]
    if bad:
        raise ValueError(f"Undeclared runtime instrumentation: {bad}")
    if initial.get("GITHUB_RUN_ATTEMPT", "1") != "1":
        raise ValueError("This confirmation plan does not permit rerun attempts")
    cpus = sorted(os.sched_getaffinity(0))[:2]
    if len(cpus) != 2:
        raise ValueError("Two CPUs required")
    plan = {"schema": 1, "replicate": args.replicate, "replicates": HOSTS,
            "driverSha": git(driver, "rev-parse", "HEAD"), "experimentSha": EXPERIMENT,
            "commits": {"old": BASE, "candidate": CANDIDATE}, "candidateTree": TREE,
            "host": platform.node(), "runId": initial.get("GITHUB_RUN_ID"),
            "runAttempt": 1, "runnerName": initial.get("RUNNER_NAME"),
            "imageOS": initial.get("ImageOS"), "imageVersion": initial.get("ImageVersion"),
            "machineId": Path("/etc/machine-id").read_text().strip(),
            "bootId": Path("/proc/sys/kernel/random/boot_id").read_text().strip(),
            "cpuAffinity": cpus, "order": list(runner.ORDER), "primaryMetrics": PRIMARY,
            "aaOrder": AA_ORDER, "aaCases": [case_name(c) for c in FOCUS],
            "aaPlacement": "All eight same-old-binary slots AFTER the complete paired experiment; no noise subtraction",
            "diagnosticPlacement": "Separate Unary-only JIT processes AFTER timing and AA; no timing inclusion",
            "stage2Authorized": False, "originalDevRetested": False,
            "frozenRunnerSha256": common.digest(experiment / "eng/issue775/boundary_pair.py")}
    common.save(out / "confirmation-plan.json", plan)
    runner.COMMITS = plan["commits"]
    # No order/workload/iteration/budget changes, and no instrumentation in the paired experiment.
    saved_argv = sys.argv
    try:
        sys.argv = [str(experiment / "eng/issue775/boundary_pair.py"), "--old", str(old),
                    "--candidate", str(candidate), "--output", str(out / "measurements")]
        runner.main()
    finally:
        sys.argv = saved_argv
    provenance = json.loads((out / "measurements/provenance.json").read_text())
    if provenance["cpuAffinity"] != cpus:
        raise ValueError("Affinity changed")
    def hashes(root):
        return {p.name: common.digest(p) for p in sorted((root / Path(common.BENCHMARK).parent).glob("*.dll"))}
    def arm_env(arm):
        return initial | {"SharpLinkStaticBindingPrototype": "true", "SHARPLINK_EXPECTED_MAGIC": "183",
                          "SHARPLINK_BENCHMARK_SHA": plan["commits"][arm], "DOTNET_PROCESSOR_COUNT": "2",
                          "DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "1"}
    affinity = ["taskset", "-c", ",".join(map(str, cpus))]
    failures = []
    aa = {"order": AA_ORDER, "sameCheckout": str(old), "assemblySha256": hashes(old),
          "slots": [], "passed": False}
    if aa["assemblySha256"] != provenance["assemblies"]["old"]:
        raise ValueError("AA substituted the old binaries")
    for i, label in enumerate(AA_ORDER, 1):
        slot = out / "aa" / f"{i:02d}-{label}"
        aa["slots"].append({"path": str(slot.relative_to(out)), "label": label,
                            "arm": "old", "assemblySha256": aa["assemblySha256"]})
        for case in FOCUS:
            directory = slot / case_name(case)
            argv = affinity + ["dotnet", common.BENCHMARK, "--static-binding-evidence", *map(str, case),
                               "10000", "100000", str(directory / "result.json")]
            if not common.execute(argv, old, directory, arm_env("old"), 120):
                failures.append(str(directory.relative_to(out)))
                continue
            try:
                audit.validate_rpc(json.loads((directory / "result.json").read_text()), "B", case_name(case))
            except Exception as error:
                failures.append(f"{directory.relative_to(out)}: {error}")
        common.save(out / "aa-plan.json", aa)
    for arm, root in (("old", old), ("candidate", candidate)):
        if hashes(root) != provenance["assemblies"][arm]:
            failures.append(arm + ": timing binaries changed")
    aa["passed"] = not failures
    common.save(out / "aa-plan.json", aa)
    common.save(out / "confirmation-failures.json", failures)
    if failures:
        raise RuntimeError(str(failures))
    # Diagnostic collection cannot change the outcome of the earlier timing data.
    # Missing/fragmented Tier1 is explicitly recorded, not retried or replaced by Tier0.
    diagnostics = []
    for arm, root in (("old", old), ("candidate", candidate)):
        for t in ("tcp", "sharedmemory"):
            directory = out / "unary-jit" / arm / t
            directory.mkdir(parents=True, exist_ok=True)
            env = arm_env(arm) | {"DOTNET_JitDisasm": "*SharpLinkClient:InvokeUnary* *SharpLinkClient:InvokeStaticUnary* *SharpLinkClient:InvokeUnaryCore* *SharpLinkServer:DispatchRpcAsync",
                                 "DOTNET_JitStdOutFile": str(directory / "disassembly.txt")}
            common.save(directory / "diagnostic-environment.json", {k: v for k, v in env.items() if k.startswith("DOTNET_")})
            argv = affinity + ["dotnet", common.BENCHMARK, "--static-binding-evidence", t, "unary", "8", "4",
                               "10000", "300000", str(directory / "result.json")]
            success = common.execute(argv, root, directory, env, 180)
            error = None
            if success:
                try:
                    audit.validate_rpc(json.loads((directory / "result.json").read_text()), "B", f"{t}-unary-c8-p4", 300000)
                except Exception as exc:
                    error = str(exc)
            text = (directory / "disassembly.txt").read_text(errors="replace") if (directory / "disassembly.txt").exists() else ""
            headers = [s for s in text.splitlines() if s.startswith("; Assembly listing for method") and "(Tier1)" in s]
            diagnostics.append({"arm": arm, "transport": t, "executionPassed": success and error is None,
                                "error": error, "tier1Headers": headers, "manualReviewRequired": True})
    common.save(out / "unary-jit/coverage.json", diagnostics)
    common.save(out / "completed.json", {"replicate": args.replicate, "executionValidated": True,
        "stage2Authorized": False, "canonicalRpcExecutions": 64, "bdnActual": 720,
        "allocationCases": 32, "aaRpcExecutions": 24, "jitRpcExecutions": 4,
        "diagnosticFailures": sum(not d["executionPassed"] for d in diagnostics),
        "note": "Integrity is not performance acceptance; missing Tier1 is not retried."})


if __name__ == "__main__":
    main()
