#!/usr/bin/env python3
"""One-factor removal experiments; diagnostic overlays never become shipping source."""
import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import statistics
import subprocess

from run import BENCHMARK, PROJECT, SCENARIOS, digest, execute, git, save

GROUPS = {
    "tcs": ["src/SharpLink.Client/PendingClientCall.cs"],
    "full-context": ["src/SharpLink.Server/SharpLinkServer.cs", "src/SharpLink.Server/SharpLinkServer.Interceptors.cs"],
    "repeat-descriptor": ["src/SharpLink.Server/SharpLinkServer.StreamEstablishment.cs",
                          "src/SharpLink.Server/SharpLinkServer.InvocationContinuation.cs",
                          "src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs"],
}
ORDER = ("old", "new", "new", "old", "new", "old", "old", "new")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--head", type=Path, required=True)
    parser.add_argument("--previous", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    head, previous, output = args.head.resolve(), args.previous.resolve(), args.output.resolve()
    commits = {"head": git(head, "rev-parse", "HEAD"), "previous": git(previous, "rev-parse", "HEAD")}
    if commits["previous"] != "2149ca799dca38bcb7f97583572406b0d0b41890":
        raise ValueError("Unexpected previous candidate")
    if git(head, "status", "--porcelain") or git(previous, "status", "--porcelain"):
        raise ValueError("Experiments require clean original source")
    cpus = sorted(os.sched_getaffinity(0))[:2]
    if len(cpus) != 2:
        raise ValueError("Two CPUs required")
    affinity = ["taskset", "-c", ",".join(map(str, cpus))]
    env = os.environ.copy()
    env.update(DOTNET_PROCESSOR_COUNT="2", DOTNET_TieredCompilation="1", DOTNET_TieredPGO="1")
    provenance = dict(commits=commits, host=platform.node(), cpuAffinity=cpus,
                      order=ORDER, groups=GROUPS, scenarios=SCENARIOS,
                      scope="Each old arm restores exactly one change group; new arm is unmodified candidate. Same-host unprofiled measurements; no acceptance thresholds changed.")
    save(output / "provenance.json", provenance)
    roots = {"candidate": head}
    for name, files in GROUPS.items():
        root = output.parent / ("variant-" + name)
        subprocess.run(["git", "-C", str(head), "worktree", "add", "--detach", str(root), commits["head"]], check=True)
        for relative in files:
            shutil.copyfile(previous / relative, root / relative)
        patch = git(root, "diff", "--", "src")
        (output / (name + ".patch")).write_text(patch + "\n")
        roots[name] = root
    for name, root in roots.items():
        for step, command in (("restore", ["dotnet", "restore", PROJECT]),
                              ("build", ["dotnet", "build", PROJECT, "--no-restore", "-c", "Release", "-v", "minimal"])):
            if not execute(command, root, output / "build" / name / step, env, 360):
                raise RuntimeError(f"{name} {step} failed")
        provenance.setdefault("assemblies", {})[name] = {
            str(path.relative_to(root)): digest(path)
            for path in (root / Path(BENCHMARK).parent).glob("SharpLink*.dll")}
    save(output / "provenance.json", provenance)
    summaries = {}
    for group in GROUPS:
        values = {}
        for slot, arm in enumerate(ORDER, 1):
            root = roots["candidate" if arm == "new" else group]
            env["SHARPLINK_BENCHMARK_SHA"] = commits["head"] + ("" if arm == "new" else "+ablation-" + group)
            for transport in ("tcp", "sharedmemory"):
                for scenario, (warmup, operations) in SCENARIOS.items():
                    case = output / group / f"{slot:02d}-{arm}" / f"{transport}-{scenario}"
                    if not execute(affinity + ["dotnet", BENCHMARK, "--contract-routing-evidence", transport,
                            scenario, str(warmup), str(operations), str(case / "result.json")], root, case, env, 120):
                        raise RuntimeError(f"Failed experiment {case}")
                    result = json.loads((case / "result.json").read_text())
                    if not result["passed"] or result["completedOperations"] != operations:
                        raise ValueError(f"Incomplete workload {case}")
                    values.setdefault((transport, scenario), {}).setdefault(arm, []).append(result)
        summaries[group] = {}
        for (transport, scenario), arms in values.items():
            row = {}
            for field in ("throughputPerSecond", "p50Microseconds", "p99Microseconds", "p999Microseconds", "processWideAllocatedBytesPerCompletedOperation", "processCpuMicrosecondsPerCompletedOperation", "receiverCompletedThroughputPerSecond"):
                if field not in arms["new"][0]:
                    continue
                old = statistics.mean(r[field] for r in arms["old"])
                new = statistics.mean(r[field] for r in arms["new"])
                row[field] = dict(old=old, new=new, ratio=new / old if old else None,
                                  oldSlots=[r[field] for r in arms["old"]], newSlots=[r[field] for r in arms["new"]])
            summaries[group][f"{transport}/{scenario}"] = row
        save(output / "summary.json", summaries)
    save(output / "complete.json", dict(passed=True, measurementSlots=len(GROUPS) * len(ORDER) * 2 * len(SCENARIOS)))


if __name__ == "__main__":
    main()
