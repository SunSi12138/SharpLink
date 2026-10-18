#!/usr/bin/env python3
"""Fixed old-static/new-static ABBA/BAAB; original dev acceptance is a separate job."""
import argparse
from collections import defaultdict
import json
import os
from pathlib import Path
import platform
from statistics import median
import subprocess

from run import BENCHMARK, PROJECT, COMMON_INPUTS, OVERLAY, execute, git, digest, save
from summarize import actual_bdn_measurements, paired, require
from attribution_report import validate_rpc, validate_gate

COMMITS = {"old": "a473f5d3a50d3b23874f8b1bb72f46e65ff939d1",
           "candidate": "4e16bf4c7ae18edf206f3e9bf1eb369c549f860e"}
ORDER = ("old", "candidate", "candidate", "old", "candidate", "old", "old", "candidate")
CASES = [(t, s, c, p) for t in ("tcp", "sharedmemory") for s in ("unary", "oneway") for c,p in ((1,1),(8,4))]


def summarize(root):
    provenance = json.loads((root / "provenance.json").read_text())
    require(provenance["commits"] == COMMITS and tuple(provenance["order"]) == ORDER, "Source/order substitution")
    require(json.loads((root / "execution-failures.json").read_text()) == [], "Failed commands")
    values = defaultdict(lambda: {"base": [], "head": []})
    runtimes = set()
    end = 0
    for index, arm in enumerate(ORDER, 1):
        slot = root / "slots" / f"{index:02d}-{arm}"
        label = "base" if arm == "old" else "head"
        def check(directory, timed):
            nonlocal end
            record = json.loads((directory / "command.json").read_text())
            require(record["exitCode"] == 0, "Failed command")
            require(record["startedUnixSeconds"] >= end, "Overlapping or reordered command")
            end = record["completedUnixSeconds"]
            env = record["environment"]
            require(env["SharpLinkStaticBindingPrototype"] == "true" and env["SHARPLINK_EXPECTED_MAGIC"] == "183" and
                    env["SHARPLINK_BENCHMARK_SHA"] == COMMITS[arm], "Wrong dialect/source")
            if timed:
                require(record["argv"][:3] == ["taskset", "-c", ",".join(map(str, provenance["cpuAffinity"]))], "Affinity mismatch")
                require(env["DOTNET_PROCESSOR_COUNT"] == "2" and env["DOTNET_TieredPGO"] == env["DOTNET_TieredCompilation"] == "1", "Runtime knobs")
        check(slot / "bdn-command", True)
        for case, samples in actual_bdn_measurements(slot / "bdn").items():
            values["BDN/" + case][label].append(median(median(samples[i:i+15]) for i in (0,15,30)))
        for t,s,c,p in CASES:
            case = f"{t}-{s}-c{c}-p{p}"
            directory = slot / "rpc" / case
            check(directory, True)
            result = json.loads((directory / "result.json").read_text())
            runtimes.add(result["Runtime"])
            for metric, value in validate_rpc(result, "B", case).items():
                values[case + "/" + metric][label].append(value)
            for metric in ("BindMilliseconds", "FirstCallMicroseconds"):
                values[case + "/" + metric][label].append(result[metric])
        check(slot / "allocation", False)
        validate_gate(json.loads((slot / "allocation/result.json").read_text()))
    require(len(runtimes) == 1, "Runtime mismatch")
    negative = json.loads((root / "negative-control/result.json").read_text())
    validate_gate(negative, True)
    require(json.loads((root / "negative-control/command.json").read_text())["exitCode"] != 0, "Negative control passed")
    result = {"commits": COMMITS, "runtime": runtimes.pop(), "comparisons": {k: paired(v) for k,v in values.items()},
              "stage2Authorized": False, "executionAndAllocationPassed": True, "negativeControl": negative,
              "meaning": "base/denominator is OLD STATIC, head/numerator is NEW STATIC. NOT original dev baseline.",
              "limits": "Four adjacent pairs; exploratory log-t 95% intervals, df=3, no multiplicity correction. No equivalence margin. No samples discarded. Both BDN labels run the same OneWay expression."}
    save(root / "summary.json", result)
    lines = ["# Old-static versus candidate — ABBA/BAAB", "", result["meaning"], "", result["limits"], "",
             "| Case | New/old throughput | Paired geometric 95% CI | Receiver p99 ratio | CPU ratio |",
             "|---|---:|---|---:|---:|"]
    for t,s,c,p in CASES:
        case = f"{t}-{s}-c{c}-p{p}"
        def metric(name): return result["comparisons"][case + "/" + name]
        rate = metric("ReceiverOperationsPerSecond")
        lo,hi = rate["pairedRatio95Ci"]
        lines.append(f"| {case} | {rate['headOverBase']:.4f} | {lo:.4f}–{hi:.4f} | {metric('Receiver-p99')['headOverBase']:.4f} | {metric('CpuMilliseconds')['headOverBase']:.4f} |")
    for label in ("PayloadSize=16", "PayloadSize=256"):
        value = result["comparisons"]["BDN/" + label]
        lo,hi = value["pairedRatio95Ci"]
        lines.append(f"\nBDN {label} time (lower better): new/old {value['headOverBase']:.4f}; CI {lo:.4f}–{hi:.4f}.")
    (root / "summary.md").write_text("\n".join(lines) + "\n")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--old", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    roots = {"old": args.old.resolve(), "candidate": args.candidate.resolve()}
    out = args.output.resolve()
    require(not out.exists() or not any(out.iterdir()), "Do not replace evidence")
    out.mkdir(parents=True, exist_ok=True)
    provenance = {"commits": COMMITS, "order": ORDER, "host": platform.node(), "cpuAffinity": sorted(os.sched_getaffinity(0))[:2],
                  "driverSha": git(Path(__file__).resolve().parents[2], "rev-parse", "HEAD"), "common": {}, "assemblies": {},
                  "warmup": 10000, "operations": 100000, "sdk": {}, "stage2Authorized": False}
    for arm, root in roots.items():
        require(git(root, "rev-parse", "HEAD") == COMMITS[arm] and not git(root, "status", "--porcelain"), "Wrong/dirty source")
        provenance["sdk"][arm] = subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip()
    require(len(set(provenance["sdk"].values())) == 1, "SDK mismatch")
    for relative in COMMON_INPUTS + OVERLAY:
        require((roots["old"] / relative).read_bytes() == (roots["candidate"] / relative).read_bytes(), "Changed workload/policy")
        provenance["common"][relative] = digest(roots["old"] / relative)
    canonical = os.environ.copy()
    require(not any(k.startswith(("DOTNET_", "COMPlus_", "CORECLR_", "COR_")) and any(s in k.lower() for s in ("jit", "tier", "profiler", "processor_count")) for k in canonical), "Diagnostic environment")
    require(len(provenance["cpuAffinity"]) == 2, "Two CPUs required")
    timing = canonical | {"DOTNET_PROCESSOR_COUNT": "2", "DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "1"}
    affinity = ["taskset", "-c", ",".join(map(str,provenance["cpuAffinity"]))]
    def env(arm, initial):
        return initial | {"SharpLinkStaticBindingPrototype": "true", "SHARPLINK_EXPECTED_MAGIC": "183", "SHARPLINK_BENCHMARK_SHA": COMMITS[arm]}
    def hashes(root): return {p.name: digest(p) for p in sorted((root / Path(BENCHMARK).parent).glob("*.dll"))}
    save(out / "provenance.json", provenance)
    failures = []
    for arm, root in roots.items():
        for name, command in (("environment",["dotnet","--info"]), ("cpu",["lscpu"]),
            ("build",["dotnet","build",PROJECT,"-c","Release","-v","minimal"]),
            ("smoke",["dotnet",BENCHMARK,"--static-binding-smoke"])):
            if not execute(command,root,out / arm / name,env(arm,timing),300):
                raise RuntimeError(f"{arm}/{name} failed")
        provenance["assemblies"][arm] = hashes(root)
    save(out / "provenance.json", provenance)
    for i,arm in enumerate(ORDER,1):
        root = roots[arm]
        slot = out / "slots" / f"{i:02d}-{arm}"
        if not execute(affinity + ["dotnet","run","--project",PROJECT,"-c","Release","--no-build","--no-restore","--",
            "--filter","*UnaryBenchmarks.Rpc_OnewayPublish*","--launchCount","3","--warmupCount","6","--iterationCount","15",
            "--invocationCount","4096","--exporters","json","--artifacts",str(slot / "bdn"),"--keepFiles","--buildTimeout","180"],
            root,slot / "bdn-command",env(arm,timing),300): failures.append(f"{i}/bdn")
        for t,s,c,p in CASES:
            directory = slot / "rpc" / f"{t}-{s}-c{c}-p{p}"
            if not execute(affinity + ["dotnet",BENCHMARK,"--static-binding-evidence",t,s,str(c),str(p),"10000","100000",str(directory / "result.json")],
                root,directory,env(arm,timing),120): failures.append(str(directory))
        gate = env(arm,canonical) | {"SHARPLINK_ALLOCATION_OUTPUT": str(slot / "allocation/result.json"),
                                   "SHARPLINK_ALLOCATION_BUDGETS": str(root / "eng/perf/allocation-budgets.json")}
        if not execute(["bash","eng/run-allocation-gate.sh"],root,slot / "allocation",gate,180): failures.append(f"{i}/allocation")
        save(out / "execution-failures.json", failures)
    gate = env("candidate",canonical) | {"SHARPLINK_ALLOCATION_OUTPUT": str(out / "negative-control/result.json"),
        "SHARPLINK_ALLOCATION_BUDGETS": str(roots["candidate"] / "eng/perf/allocation-budgets.json")}
    if not execute(["bash","eng/run-allocation-gate.sh","--filter","rpc-add-sharedmemory-c1","--inject-bytes-per-operation","512"],
        roots["candidate"],out / "negative-control",gate,120,expected_failure=True): failures.append("Negative control passed")
    for arm,root in roots.items():
        if hashes(root) != provenance["assemblies"][arm]: failures.append(f"{arm}: binaries changed")
    save(out / "execution-failures.json", failures)
    try: summarize(out)
    except Exception as error: failures.append(str(error))
    save(out / "execution-failures.json", failures)
    if failures: raise RuntimeError("; ".join(failures))


if __name__ == "__main__":
    main()
