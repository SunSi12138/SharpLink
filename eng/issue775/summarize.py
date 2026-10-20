#!/usr/bin/env python3
"""Validate complete ABBA/BAAB inputs without changing any performance budget."""
import argparse
import json
import math
from pathlib import Path
from statistics import median, mean, stdev
from collections import defaultdict

ORDER = ("base", "head", "head", "base", "head", "base", "base", "head")
METRICS = ("SenderOperationsPerSecond", "ReceiverOperationsPerSecond", "CpuMilliseconds",
           "ProcessBytesPerCompletedOperation", "DrainSeconds")

def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def successful_command(directory):
    require(read(directory / "command.json")["exitCode"] == 0, f"Command failed: {directory}")


def actual_bdn_measurements(directory):
    files = list(directory.glob("results/*-report-full-compressed.json"))
    require(len(files) == 1, f"Expected exactly one BDN result in {directory}")
    document = read(files[0])
    result = {}
    for benchmark in document["Benchmarks"]:
        require(benchmark["Method"] == "Rpc_OnewayPublish", "Unexpected benchmark method")
        label = benchmark["Parameters"]
        require(label not in result, f"Duplicate BDN label {label}")
        actual = [item for item in benchmark["Measurements"]
                  if item["IterationMode"] == "Workload" and item["IterationStage"] == "Actual"]
        require(len(actual) == 45, f"{label}: expected 3 launches by 15 actual iterations")
        require({item["LaunchIndex"] for item in actual} == {1, 2, 3}, "Missing BDN launch")
        for launch in (1, 2, 3):
            indices = [item["IterationIndex"] for item in actual if item["LaunchIndex"] == launch]
            require(sorted(indices) == list(range(1, 16)), "Duplicate or missing actual iteration")
        require(all(item["Operations"] == 4096 and item["Nanoseconds"] > 0 for item in actual),
                "Unexpected BDN operation count or invalid timing")
        actual.sort(key=lambda item: (item["LaunchIndex"], item["IterationIndex"]))
        result[label] = [item["Nanoseconds"] / item["Operations"] for item in actual]
    require(set(result) == {"PayloadSize=16", "PayloadSize=256"}, "Unexpected BDN parameter set")
    return result


def compare(values):
    base = median(values["base"])
    head = median(values["head"])
    return {"baseMedian": base, "headMedian": head,
            "headOverBase": head / base if base > 0 else None,
            "headMinusBase": head - base,
            "baseSamples": values["base"], "headSamples": values["head"]}


def paired(values):
    base = values["base"]
    head = values["head"]
    require(len(base) == len(head) == 4, "Exactly four matched slots per arm required")
    result = compare(values)
    if all(x > 0 for x in base + head):
        logs = [math.log(h / b) for b, h in zip(base, head)]
        center = mean(logs)
        radius = 3.182446305 * stdev(logs) / 2
        result["pairedGeometricRatio"] = math.exp(center)
        result["pairedRatio95Ci"] = [math.exp(center - radius), math.exp(center + radius)]
    return result


def summarize(root):
    provenance = read(root / "provenance.json")
    require(tuple(provenance["order"]) == ORDER, "Execution order mismatch")
    require(provenance["commits"]["base"] == "1a54993d3550911f5979a7b4d3f9f5f63ee86a64", "Baseline substitution")
    scenarios = [f"{t}-{s}-c{c}-p{p}" for t in ("tcp", "sharedmemory")
                 for s in ("unary", "oneway") for c, p in ((1, 1), (8, 4))]
    rpc = defaultdict(lambda: defaultdict(lambda: defaultdict(list)))
    bdn = defaultdict(lambda: defaultdict(list))
    allocation = defaultdict(lambda: defaultdict(list))
    gates = True
    runtimes = set()
    for index, arm in enumerate(ORDER, 1):
        slot = root / "slots" / f"{index:02d}-{arm}"
        successful_command(slot / "bdn-command")
        for label, samples in actual_bdn_measurements(slot / "bdn").items():
            # median(15 actual iterations) -> median(3 launches) -> 4 independent arm slots
            bdn[label][arm].append(median(median(samples[i:i+15]) for i in range(0, 45, 15)))
        for scenario in scenarios:
            directory = slot / "rpc" / scenario
            successful_command(directory)
            report = read(directory / "result.json")
            require(report["Passed"] and report["Operations"] == 100000 and report["Warmup"] == 10000,
                    "Changed or unsuccessful workload")
            require(report["Dialect"] == ("static-binding" if arm == "head" else "original-dev"), "Wrong compiled arm")
            require(report["RoutePrefixBytes"] == 16, "Unexpected prefix size")
            require(report["ProcessorCount"] == 2, "CPU configuration mismatch")
            runtimes.add(report["Runtime"])
            if arm == "head":
                require(report["BoundRoutes"] == report["Pool"] and report["BindCount"] == report["Pool"] and
                        report["PendingBinds"] == 0, "Steady-state rebinding or incomplete route preparation")
            measurement = report["Measurement"]
            require(measurement["SenderCompleted"] == measurement["ReceiverCompleted"] == 100000,
                    "Not all operations reached completion")
            for sample_key in ("SenderLatencyTicks", "ReceiverLatencyTicks"):
                samples = measurement[sample_key]
                require(len(samples) == 100000 and all(x >= 0 for x in samples), "Raw samples incomplete")
            for metric in METRICS:
                value = measurement[metric]
                require(math.isfinite(value) and value >= 0, "Invalid metric")
                rpc[scenario][metric][arm].append(value)
            for kind in ("Sender", "Receiver"):
                for q, value in zip(("p50", "p99", "p999"), measurement[f"{kind}P50P99P999Microseconds"]):
                    rpc[scenario][f"{kind}-{q}-us"][arm].append(value)
            for metric in ("BindMilliseconds", "FirstCallMicroseconds"):
                rpc[scenario][metric][arm].append(report[metric])
        gate = read(slot / "allocation/result.json")
        command = read(slot / "allocation/command.json")
        gates = gates and gate["passed"] and command["exitCode"] == 0
        require(len(gate["cases"]) == 4, "Missing canonical allocation case")
        for case in gate["cases"]:
            allocation[case["name"]][arm].append(case)
    require(len(runtimes) == 1, "Runtime version changed within matched experiment")
    negative = read(root / "negative-control/result.json")
    require(read(root / "negative-control/command.json")["exitCode"] != 0 and not negative["passed"],
            "Negative control unexpectedly passed")
    require(len(negative["cases"]) == 1 and negative["cases"][0]["medianBytesPerOperation"] >
            negative["cases"][0]["maxBytesPerOperation"], "Negative control failed for an unrelated reason")
    result = {
        "schemaVersion": 1, "commits": provenance["commits"], "runtime": next(iter(runtimes)),
        "passedExecutionAndAllocationGates": gates,
        "stage2Authorized": False,
        "interpretation": "Exploratory paired measurements, not a production release or equivalence claim. No new tolerance or rebaseline is introduced.",
        "uncertainty": "95% t intervals use four adjacent A/B log-ratios (df=3). They assume independent pairs and approximate normality; no multiple-comparison correction. Tail estimates and hosted-runner noise remain relevant.",
        "rpc": {case: {metric: paired(values) for metric, values in metrics.items()} for case, metrics in rpc.items()},
        "onewaySenderBdnNanoseconds": {label: paired(values) for label, values in bdn.items()},
        "allocation": dict(allocation), "negativeControl": negative,
    }
    (root / "summary.json").write_text(json.dumps(result, indent=2) + "\n")
    lines = ["# Static binding prototype — same-host ABBA/BAAB", "",
             f"Baseline `{provenance['commits']['base']}`; candidate `{provenance['commits']['head']}`.", "",
             "Execution/allocation gates: " + ("PASS" if gates else "FAIL") + ". This is not a phase-2 or production approval.", "",
             "| Scenario | A completed ops/s | B completed ops/s | B/A | Paired 95% CI | B/A receiver p99 |",
             "|---|---:|---:|---:|---|---:|"]
    for case, metrics in result["rpc"].items():
        q = metrics["ReceiverOperationsPerSecond"]
        ci = q["pairedRatio95Ci"]
        lines.append(f"| {case} | {q['baseMedian']:.0f} | {q['headMedian']:.0f} | {q['headOverBase']:.4f} | {ci[0]:.4f}–{ci[1]:.4f} | {metrics['Receiver-p99-us']['headOverBase']:.4f} |")
    lines.extend(["", "Raw latency arrays, every BDN iteration, original allocation medians/spreads and negative control are retained.",
                  result["uncertainty"], "", "No non-significant difference is interpreted as proof of equivalence."])
    (root / "summary.md").write_text("\n".join(lines) + "\n")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    summarize(args.root)
