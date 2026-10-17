#!/usr/bin/env python3
"""Validate raw attribution evidence, keeping diagnostics separate from uninstrumented results."""
import argparse
from collections import defaultdict
import json
import math
from pathlib import Path
from statistics import mean, median, stdev

from summarize import actual_bdn_measurements, require

COMMITS = {"A": "1a54993d3550911f5979a7b4d3f9f5f63ee86a64",
           "B": "d2cb248b5e4b573d448092f7f152c2f2dd281e30",
           "C": "d2cb248b5e4b573d448092f7f152c2f2dd281e30"}
BLOCKS = ("ABC", "BCA", "CAB", "CBA", "ACB", "BAC")
AA_ORDER = "XYYXYXXY"
CASES = tuple(f"{t}-{s}-c{c}-p{p}" for t in ("tcp", "sharedmemory") for s in ("unary", "oneway")
              for c, p in (((1, 1), (1, 4), (8, 1), (8, 4)) if s == "unary" else ((1, 1), (8, 4))))
FOCUS = ("tcp-unary-c8-p4", "tcp-oneway-c1-p1")
BUDGETS = {"rpc-add-sharedmemory-c1": (1450, 50), "rpc-add-sharedmemory-c8": (650, 90),
           "rpc-oneway-sharedmemory-c1": (960, 20), "send-pump-idle-wake-balanced": (700, 5)}


def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


def close(actual, expected, label):
    require(math.isfinite(actual) and math.isclose(actual, expected, rel_tol=1e-10, abs_tol=1e-8), label)


def validate_rpc(value, arm, case, operations=100000):
    require(arm in COMMITS, "Unknown arm")
    require(case == f"{value['Transport']}-{value['Shape']}-c{value['Concurrency']}-p{value['Pool']}", "Case substitution")
    require(value["Schema"] == 1 and value["Passed"] is True, "Failed RPC")
    require(value["Dialect"] == ("static-binding" if arm == "B" else "original-dev"), "Wrong wire dialect")
    require(value["Operations"] == operations and value["Warmup"] == 10000, "Changed operation count")
    require(value["ProcessorCount"] == 2 and value["RoutePrefixBytes"] == 16, "CPU or prefix changed")
    require(value["BoundRoutes"] == value["Pool"] and value["PendingBinds"] == 0, "Incomplete bindings")
    require(value["BindCount"] == (value["Pool"] if arm == "B" else 0), "Unexpected Bind count")
    measurement = value["Measurement"]
    require(measurement["SenderCompleted"] == measurement["ReceiverCompleted"] == operations, "Incomplete work")
    frequency = value["StopwatchFrequency"]
    require(isinstance(frequency, int) and frequency > 0, "Invalid clock frequency")
    metrics = {}
    for side in ("Sender", "Receiver"):
        raw = measurement[side + "LatencyTicks"]
        require(len(raw) == operations and all(type(x) is int and x >= 0 for x in raw), "Missing/invalid raw latency")
        samples = sorted(raw)
        expected = [samples[min(operations-1, math.ceil(q * operations)-1)] * 1e6 / frequency for q in (0.5, 0.99, 0.999)]
        quantiles = measurement[side + "P50P99P999Microseconds"]
        require(len(quantiles) == 3, "Incomplete quantiles")
        for name, actual, calculated in zip(("p50", "p99", "p999"), quantiles, expected):
            close(actual, calculated, "Quantile differs from raw records")
            metrics[side + "-" + name] = actual
        seconds = measurement[side + "Seconds"]
        require(math.isfinite(seconds) and seconds > 0, "Invalid time boundary")
        throughput = measurement[side + "OperationsPerSecond"]
        close(throughput, operations / seconds, "Incorrect throughput denominator")
        metrics[side + "OperationsPerSecond"] = throughput
    if value["Shape"] == "unary":
        require(measurement["SenderLatencyTicks"] == measurement["ReceiverLatencyTicks"], "Unary boundary mismatch")
    for key in ("CpuMilliseconds", "ProcessBytesPerCompletedOperation", "DrainSeconds"):
        require(math.isfinite(measurement[key]) and measurement[key] >= 0, "Invalid metric")
        metrics[key] = measurement[key]
    close(measurement["DrainSeconds"], max(0, measurement["ReceiverSeconds"]-measurement["SenderSeconds"]), "Incorrect drain")
    return metrics


def compare(left, right):
    require(len(left) == len(right) and len(left) in (4, 6), "Incorrect paired sample count")
    require(all(math.isfinite(x) and x >= 0 for x in left + right), "Invalid comparison samples")
    result = {"denominatorSamples": left, "numeratorSamples": right,
              "denominatorMedian": median(left), "numeratorMedian": median(right),
              "medianRatio": median(right) / median(left) if median(left) > 0 else None}
    if min(left + right) > 0:
        logs = [math.log(b/a) for a, b in zip(left, right)]
        center = mean(logs)
        radius = {4: 3.182446305, 6: 2.570581836}[len(logs)] * stdev(logs) / math.sqrt(len(logs))
        result.update(pairedGeometricRatio=math.exp(center), paired95Ci=[math.exp(center-radius), math.exp(center+radius)])
    return result


def expected_executions():
    result = [(f"aa/{i:02d}-{label}", "A", label, "aa", (i-1)//2, list(FOCUS)) for i, label in enumerate(AA_ORDER[:4], 1)]
    result += [(f"matrix/{block:02d}/{pos}-{arm}", arm, arm, "matrix", block, list(CASES))
               for block, order in enumerate(BLOCKS, 1) for pos, arm in enumerate(order, 1)]
    result += [(f"aa/{i:02d}-{label}", "A", label, "aa", (i-1)//2, list(FOCUS)) for i, label in enumerate(AA_ORDER[4:], 5)]
    return result


def validate_gate(gate, negative=False):
    require(gate["configuration"] == "Release" and gate["runtimeMajor"] == 10, "Wrong allocation configuration")
    cases = gate["cases"]
    require(len(cases) == (1 if negative else 4), "Missing allocation cases")
    require({c["name"] for c in cases} == ({"rpc-add-sharedmemory-c1"} if negative else set(BUDGETS)), "Wrong allocation cases")
    require(gate["injectedBytesPerOperation"] == (512 if negative else 0), "Changed negative control")
    for case in cases:
        budget = BUDGETS[case["name"]]
        require((case["maxBytesPerOperation"], case["maxSpreadBytesPerOperation"]) == budget, "Allocation rebaseline")
        samples = case["samples"]
        require(len(samples) == case["sampleCount"] == 5, "Missing allocation samples")
        values = []
        for index, sample in enumerate(samples):
            require(sample["index"] == index and sample["completedOperations"] == {"rpc-add-sharedmemory-c1": 4000, "rpc-add-sharedmemory-c8": 4096, "rpc-oneway-sharedmemory-c1": 4000, "send-pump-idle-wake-balanced": 2000}[case["name"]], "Allocation work incomplete")
            value = sample["allocatedBytes"] / sample["completedOperations"]
            close(sample["bytesPerOperation"], value, "Invalid allocation division")
            values.append(value)
        close(case["medianBytesPerOperation"], median(values), "Invalid allocation median")
        close(case["spreadBytesPerOperation"], max(values)-min(values), "Invalid allocation spread")
        passed = median(values) <= budget[0] and max(values)-min(values) <= budget[1]
        require(case["passed"] == passed, "Incorrect allocation result")
        if negative:
            require(not passed and median(values) > budget[0], "Negative control failed for unrelated reason")
        else:
            require(passed, "Canonical allocation gate failed")
    require(gate["passed"] is (not negative), "Unexpected allocation result")


def report(root):
    manifest = read(root / "manifest.json")
    require(manifest["mode"] == "timing" and manifest["commits"] == COMMITS, "Profile mixing or source substitution")
    require(tuple(manifest["blocks"]) == BLOCKS and manifest["aaOrder"] == AA_ORDER, "Changed run order")
    require(tuple(manifest["cases"]) == CASES and tuple(manifest["focus"]) == FOCUS, "Changed matrix")
    require(manifest["warmup"] == 10000 and manifest["operations"] == 100000, "Changed workload")
    require(manifest["stage2Authorized"] is False, "No phase-2 decision belongs in diagnostics")
    executions = read(root / "executions.json")
    require(len(executions) == 26, "Missing or extra execution slots")
    grouped = {"aa": defaultdict(lambda: defaultdict(list)), "matrix": defaultdict(lambda: defaultdict(list))}
    runtimes = set()
    last_end = 0
    def command(directory, arm):
        nonlocal last_end
        value = read(directory / "command.json")
        require(value["exitCode"] == 0, f"Failed command: {directory}")
        require(value["startedUnixSeconds"] >= last_end and value["completedUnixSeconds"] >= value["startedUnixSeconds"], "Overlapping or reordered jobs")
        last_end = value["completedUnixSeconds"]
        env = value["environment"]
        require(env["SHARPLINK_BENCHMARK_SHA"] == COMMITS[arm], "Command SHA mismatch")
        require(env["SHARPLINK_EXPECTED_MAGIC"] == ("183" if arm == "B" else "137"), "Missing child dialect guard")
        require(env["SharpLinkStaticBindingPrototype"] == ("true" if arm == "B" else "false"), "Wrong build flag")
        require(env["DOTNET_PROCESSOR_COUNT"] == "2" and env["DOTNET_TieredCompilation"] == "1" and env["DOTNET_TieredPGO"] == "1", "Mismatched runtime knobs")
        require(value["argv"][:3] == ["taskset", "-c", ",".join(map(str, manifest["cpuAffinity"]))], "Mismatched CPU affinity")
    for execution, expected in zip(executions, expected_executions()):
        fields = ("path", "arm", "label", "group", "block", "cases")
        require(tuple(execution[k] for k in fields) == expected, "Missing, swapped or duplicated slot")
        require(execution["assemblySha256"] == manifest["assemblies"][execution["arm"]], "A/A binary substitution")
        path, arm, label, group, block, cases = expected
        slot = root / path
        command(slot / "bdn-command", arm)
        for name, samples in actual_bdn_measurements(slot / "bdn").items():
            grouped[group]["BDN/" + name][label].append(median(median(samples[i:i+15]) for i in (0, 15, 30)))
        for case in cases:
            directory = slot / "rpc" / case
            command(directory, arm)
            value = read(directory / "result.json")
            runtimes.add(value["Runtime"])
            for metric, number in validate_rpc(value, arm, case).items():
                grouped[group][case + "/" + metric][label].append(number)
    require(len(runtimes) == 1, "Runtime mismatch")
    allocation = {}
    for arm in COMMITS:
        require(read(root / "allocation" / arm / "command.json")["exitCode"] == 0, "Allocation runner failed")
        allocation[arm] = read(root / "allocation" / arm / "result.json")
        validate_gate(allocation[arm])
    negative = read(root / "negative-control/result.json")
    require(read(root / "negative-control/command.json")["exitCode"] != 0, "Negative control passed")
    validate_gate(negative, True)
    require(read(root / "execution-failures.json") == [], "Command failures are retained and block approval")
    comparisons = {"Y/X": {key: compare(values["X"], values["Y"]) for key, values in grouped["aa"].items()}}
    for denominator, numerator in (("A", "B"), ("A", "C"), ("C", "B")):
        comparisons[numerator + "/" + denominator] = {key: compare(values[denominator], values[numerator])
                                                       for key, values in grouped["matrix"].items()}
    result = {"schema": 1, "commits": COMMITS, "driverSha": manifest["driverSha"], "runtime": runtimes.pop(),
              "executionValidated": True, "stage2Authorized": False, "rpcExecutions": 232, "bdnActualMeasurements": 2340,
              "comparisons": comparisons, "allocation": allocation, "negativeControl": negative,
              "limits": "Diagnosis only; no equivalence margin. Six paired ABC blocks (df=5) and four A/A pairs (df=3); log-t intervals exploratory, no multiplicity correction. A/A is not a noise subtraction. No samples discarded. Profiles are excluded."}
    (root / "summary.json").write_text(json.dumps(result, indent=2) + "\n")
    lines = ["# #775 static-path attribution", "", f"Driver `{manifest['driverSha']}`; fixed A `{COMMITS['A']}`; B/C `{COMMITS['B']}`.",
             "", "B enables binding, C disables it on the same source. X/Y share exactly the same A binaries.",
             "Execution/integrity gates passed. **Not performance acceptance; phase 2 is not authorized.**", ""]
    for comparison, values in comparisons.items():
        lines += ["## " + comparison, "", "| Case | Ratio (medians) | Paired geometric 95% CI | Receiver p99 ratio | CPU ratio |", "|---|---:|---|---:|---:|"]
        for case in (FOCUS if comparison == "Y/X" else CASES):
            ratio = values[case + "/ReceiverOperationsPerSecond"]
            ci = ratio["paired95Ci"]
            lines.append(f"| {case} | {ratio['medianRatio']:.4f} | {ci[0]:.4f}–{ci[1]:.4f} | {values[case+'/Receiver-p99']['medianRatio']:.4f} | {values[case+'/CpuMilliseconds']['medianRatio']:.4f} |")
        for label in ("PayloadSize=16", "PayloadSize=256"):
            ratio = values["BDN/" + label]
            ci = ratio["paired95Ci"]
            lines.append(f"| BDN {label} (time; lower is better) | {ratio['medianRatio']:.4f} | {ci[0]:.4f}–{ci[1]:.4f} | — | — |")
        lines.append("")
    lines += [result["limits"], "", "Both PayloadSize labels run the same original OneWay expression, not different actual payloads."]
    (root / "summary.md").write_text("\n".join(lines) + "\n")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    report(parser.parse_args().root)
