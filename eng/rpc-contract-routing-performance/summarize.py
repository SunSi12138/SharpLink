#!/usr/bin/env python3
"""Validate raw matched evidence and report separate sender, receiver, tail and allocation metrics."""

import argparse
from collections import defaultdict
import json
import math
from pathlib import Path
from statistics import median

ORDER = ("base", "head", "head", "base", "head", "base", "base", "head")
OPERATION_COUNTS = {
    "rpc-add-sharedmemory-c1": 4_000, "rpc-add-sharedmemory-c8": 4_096,
    "rpc-oneway-sharedmemory-c1": 4_000, "send-pump-idle-wake-balanced": 2_000,
}
METRICS = (
    "throughputPerSecond", "receiverCompletedThroughputPerSecond", "p50Microseconds",
    "p99Microseconds", "p999Microseconds", "processWideAllocatedBytesPerCompletedOperation",
    "processCpuMicrosecondsPerCompletedOperation", "drainSecondsAfterSendCompletion",
)


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


def summarize(root):
    provenance = read(root / "provenance.json")
    require(provenance["schemaVersion"] == 1 and tuple(provenance["order"]) == ORDER,
            "Unknown evidence schema or unmatched execution order")
    expected_slots = [f"{index:02d}-{arm}" for index, arm in enumerate(ORDER, 1)]
    require(sorted(item.name for item in (root / "slots").iterdir()) == expected_slots,
            "Missing, duplicate or unexpected timing slots")
    rpc_values = defaultdict(lambda: defaultdict(lambda: defaultdict(list)))
    sender_values = defaultdict(lambda: defaultdict(list))
    allocation_values = defaultdict(lambda: defaultdict(list))
    allocations_passed = True
    completed_rpc = 0
    completed_bdn = 0
    completed_allocation = 0
    identities = set()
    allocation_policy = None
    for slot_name, arm in zip(expected_slots, ORDER):
        slot = root / "slots" / slot_name
        successful_command(slot / "bdn-command")
        for label, samples in actual_bdn_measurements(slot / "bdn").items():
            # Preserve #802's hierarchy: median of 15 actual iterations per launch,
            # then median of three launches per slot, then median of four arm slots.
            sender_values[label][arm].append(median(median(samples[start:start + 15])
                                                   for start in (0, 15, 30)))
            completed_bdn += len(samples) * 4096
        expected_cases = {f"{transport}-{scenario}" for transport in provenance["transports"]
                          for scenario in provenance["scenarios"]}
        require({item.name for item in (slot / "rpc").iterdir()} == expected_cases,
                f"{slot_name}: incomplete RPC scenario matrix")
        for case in sorted((slot / "rpc").iterdir()):
            successful_command(case)
            result = read(case / "result.json")
            require(result["passed"] and result["schemaVersion"] == 1, f"{case}: RPC did not pass")
            require(result["commit"] == provenance["commits"][arm], "Mixed source identities")
            require(result["hostName"] == provenance["hostName"], "RPC measurements used different hosts")
            require(result["processorCount"] == 2 and result["performanceProfile"] == "Balanced" and
                    result["connections"] == result["concurrency"] == 1,
                    "RPC process or connection configuration differs from the declared workload")
            identities.add((result["runtime"], result["operatingSystem"], result["architecture"],
                            result["processorCount"], result["serverGc"], result["performanceProfile"],
                            result["connections"], result["concurrency"]))
            warmup, count = provenance["scenarios"][result["scenario"]]
            require(result["warmupOperations"] == warmup and result["requestedOperations"] == count,
                    "Measured workload configuration differs")
            require(result["completedOperations"] == count and result["receiverCompletedOperations"] == count,
                    "Missing receiver-completed operations")
            require(0 <= result["receiverObservedAtSendCompletion"] <= count and
                    result["stopwatchFrequency"] > 0 and
                    result["streamItemsPerOperation"] == (32 if result["scenario"] in ("clientstream", "serverstream", "duplex") else 0),
                    "Invalid receiver observation, clock or stream item count")
            require(len(result["latencyTicks"]) == count and all(value >= 0 for value in result["latencyTicks"]),
                    "Invalid per-operation latency record")
            boundary = "sender-queued" if result["scenario"] == "oneway-sender" else "receiver-completed"
            require(result["completionBoundary"] == boundary, "Mislabelled OneWay completion boundary")
            require(case.name == f"{result['transport']}-{result['scenario']}", "Scenario path does not match contents")
            require(result["elapsedSeconds"] > 0 and result["receiverCompletedElapsedSeconds"] >= result["elapsedSeconds"],
                    "Invalid elapsed or receiver-drain time")
            require(math.isclose(result["throughputPerSecond"], count / result["elapsedSeconds"], rel_tol=1e-9),
                    "Throughput does not match measured operations and elapsed time")
            require(math.isclose(result["receiverCompletedThroughputPerSecond"],
                                 count / result["receiverCompletedElapsedSeconds"], rel_tol=1e-9) and
                    math.isclose(result["drainSecondsAfterSendCompletion"],
                                 result["receiverCompletedElapsedSeconds"] - result["elapsedSeconds"],
                                 rel_tol=1e-9, abs_tol=1e-9),
                    "Receiver throughput or drain time is not reproducible")
            ordered = sorted(result["latencyTicks"])
            for metric, percentile in (("p50Microseconds", .50), ("p99Microseconds", .99), ("p999Microseconds", .999)):
                expected = ordered[math.ceil(count * percentile) - 1] * 1_000_000 / result["stopwatchFrequency"]
                require(math.isclose(result[metric], expected, rel_tol=1e-9, abs_tol=1e-9), "Tail percentile is not reproducible")
            for metric in METRICS:
                value = result[metric]
                require(math.isfinite(value) and value >= 0, f"Invalid metric {metric}")
                rpc_values[case.name][metric][arm].append(value)
            completed_rpc += count

        allocation = read(slot / "allocation/result.json")
        require(allocation["mode"] == "gate" and allocation["configuration"] == "Release" and
                allocation["injectedBytesPerOperation"] == 0 and allocation["runtimeMajor"] == 10,
                "Unexpected allocation-gate mode or runtime")
        require({case["name"] for case in allocation["cases"]} == set(OPERATION_COUNTS),
                "Incomplete canonical allocation cases")
        policy = {case["name"]: (case["sampleCount"], case["maxBytesPerOperation"], case["maxSpreadBytesPerOperation"])
                  for case in allocation["cases"]}
        if allocation_policy is None:
            allocation_policy = policy
        require(policy == allocation_policy, "Allocation budgets differ between slots")
        for case in allocation["cases"]:
            require(case["sampleCount"] == 5 and len(case["samples"]) == 5,
                    "Canonical five-sample allocation policy changed")
            require(case["operationsPerSample"] == OPERATION_COUNTS[case["name"]],
                    "Canonical allocation operation count changed")
            for sample in case["samples"]:
                require(sample["completedOperations"] == case["operationsPerSample"], "Allocation sample did not complete")
                require(math.isclose(sample["bytesPerOperation"], sample["allocatedBytes"] / sample["completedOperations"],
                                     rel_tol=1e-9), "Allocation normalization is inconsistent")
                completed_allocation += sample["completedOperations"]
            allocations_passed = allocations_passed and case["passed"]
            allocation_values[case["name"]][arm].append(case["medianBytesPerOperation"])
        allocations_passed = allocations_passed and allocation["passed"] and read(slot / "allocation/command.json")["exitCode"] == 0

    require(len(identities) == 1, f"Runtime, process or connection configuration differs: {identities}")
    negative = read(root / "negative-control/result.json")
    require(read(root / "negative-control/command.json")["exitCode"] != 0 and not negative["passed"],
            "Injected negative control unexpectedly passed")
    require(negative["injectedBytesPerOperation"] == 512 and len(negative["cases"]) == 1,
            "Invalid negative-control injection")
    rejected = negative["cases"][0]
    require(rejected["name"] == "rpc-add-sharedmemory-c1" and not rejected["passed"] and
            rejected["medianBytesPerOperation"] > rejected["maxBytesPerOperation"],
            "Negative control failed for a reason other than its allocation ceiling")
    report = {
        "schemaVersion": 1, "commits": provenance["commits"],
        "passedExecutionAndAllocationGates": allocations_passed,
        "performanceVerdict": "requires review of matched ratios; workflow success alone is not acceptance",
        "completedRpcOperations": completed_rpc, "bdnSenderQueuedOperations": completed_bdn,
        "canonicalAllocationCompletedOperations": completed_allocation,
        "senderQueuedBdnActualIterationNsPerOperation": {key: compare(value) for key, value in sender_values.items()},
        "rpc": {key: {metric: compare(value) for metric, value in metrics.items()}
                for key, metrics in rpc_values.items()},
        "canonicalAllocationBytesPerOperation": {key: compare(value) for key, value in allocation_values.items()},
    }
    (root / "summary.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    lines = ["# Matched RPC contract routing performance", "",
             f"Base `{provenance['commits']['base']}`; head `{provenance['commits']['head']}`.", "",
             "All eight ABBA/BAAB slots ran on one host with the same SDK, two-CPU affinity, Release configuration and benchmark inputs. Ratios are head/base; no throughput or tail budget is silently introduced or relaxed.", "",
             "## Historical OneWay sender workload", "",
             "BDN values use the historical median hierarchy: 15 actual iterations per launch, three launches per slot, and four slots per arm. They are not individual-request tail latency. Both inherited parameter labels send identical arguments.", "",
             "| Parameter label | Base ns/queued call | Head ns/queued call | Head/base |", "|---|---:|---:|---:|"]
    for label, item in report["senderQueuedBdnActualIterationNsPerOperation"].items():
        lines.append(f"| {label} | {item['baseMedian']:.3f} | {item['headMedian']:.3f} | {item['headOverBase']:.4f} |")
    lines += ["", "## Generated RPC throughput and per-request tails", "",
              "For `oneway-sender`, throughput and tails stop at local queue completion. Its independently observed final receiver count and drain throughput are retained in JSON. `oneway-receiver` waits for the business counter after every request and is a single-in-flight completion-barrier workload, not a maximum-throughput claim.", "",
              "| Transport/scenario | Base op/s | Head op/s | Head/base | Base p99 us | Head p99 us | Head p99.9 us |", "|---|---:|---:|---:|---:|---:|---:|"]
    for key, metrics in report["rpc"].items():
        qps, p99, p999 = (metrics[name] for name in ("throughputPerSecond", "p99Microseconds", "p999Microseconds"))
        lines.append(f"| {key} | {qps['baseMedian']:.1f} | {qps['headMedian']:.1f} | {qps['headOverBase']:.4f} | {p99['baseMedian']:.3f} | {p99['headMedian']:.3f} | {p999['headMedian']:.3f} |")
    lines += ["", "## Unchanged canonical allocation gate", "",
              "These medians retain all four checked-in budgets, five samples per case, original workloads and an explicitly rejected 512-byte negative control. Focused-runner aggregate allocations are diagnostic only and remain separate in JSON.", "",
              "| Case | Base B/op | Head B/op | Delta B/op |", "|---|---:|---:|---:|"]
    for key, item in report["canonicalAllocationBytesPerOperation"].items():
        lines.append(f"| {key} | {item['baseMedian']:.3f} | {item['headMedian']:.3f} | {item['headMinusBase']:+.3f} |")
    lines += ["", "The OneWay handler counter verifies business execution in this in-process transport fixture. It adds no protocol ACK and does not convert the public OneWay API into confirmed delivery. Process-wide allocation includes both endpoints; it does not attribute allocations to a particular thread or component.", ""]
    (root / "summary.md").write_text("\n".join(lines), encoding="utf-8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    arguments = parser.parse_args()
    print(json.dumps(summarize(arguments.root), indent=2))
