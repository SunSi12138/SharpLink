#!/usr/bin/env python3
"""Recompute issue #802's retained timing, allocation and CPU-profile evidence."""

import argparse
from collections import defaultdict
import hashlib
import json
import math
from pathlib import Path
import re
from statistics import mean, median

from summarize import ORDER, actual_bdn_measurements, read, require

BASE = "eb99fe887cf2129d9b88441245ca0a4a6406b6c2"
HEAD = "3aedcfc71e85a43fa71a266b7fd0e6d18939d4ff"
ZIP_DIGESTS = {
    "timing": "d620c2a27dae2475c28ec379444372063f727f26841f06ed7f9c8e2ebe6b03f9",
    "allocation": "49dba9d07a77a4afee8303d7d194357b6f1b68923749a73ef86c4044827f8751",
}
ADMISSION = (
    "AdmitContractCallAsync", "ReserveLiveContractConnection", "GetReadyConnection",
    "SelectContractConnection", "IsContractCompatible", "TryReserveCompatible",
    "ThrowIfContractWaitTerminated",
)
SENDER_FRAME = "SharpLink.Benchmarks!SharpLink.Benchmarks.UnaryBenchmarks.Rpc_OnewayPublish()"


def verify_identity(root):
    provenance = (root / "provenance.txt").read_text(encoding="utf-8")
    require(f"base_sha={BASE}\n" in provenance and f"production_sha={HEAD}\n" in provenance,
            "Historical artifact has different production identities")
    require(not (root / "harness-diff.txt").read_text(encoding="utf-8").strip(),
            "Historical artifact reports a changed measurement harness")


def timing(root):
    verify_identity(root)
    values = defaultdict(list)
    calls = 0
    for index, arm in enumerate(ORDER, 1):
        slot = root / "timing" / f"{index:02d}-{arm}"
        require((slot / "process-exit-code.txt").read_text().strip() == "0", "Historical timing failed")
        require(f"source_sha={BASE if arm == 'base' else HEAD}\n" in (slot / "provenance.txt").read_text(),
                "Historical timing slot has different source identity")
        for label, raw in actual_bdn_measurements(slot / "bdn").items():
            launches = [median(raw[start:start + 15]) for start in (0, 15, 30)]
            values[label].append({"slot": slot.name, "arm": arm,
                                  "launchMediansNs": launches, "slotMedianNs": median(launches)})
            calls += len(raw) * 4096
    comparisons = {}
    for label, slots in values.items():
        base = median(item["slotMedianNs"] for item in slots if item["arm"] == "base")
        head = median(item["slotMedianNs"] for item in slots if item["arm"] == "head")
        blocks = []
        for start in (0, 4):
            block = slots[start:start + 4]
            b = median(item["slotMedianNs"] for item in block if item["arm"] == "base")
            h = median(item["slotMedianNs"] for item in block if item["arm"] == "head")
            blocks.append({"order": [item["arm"] for item in block], "baseNs": b,
                           "headNs": h, "headOverBase": h / b})
        comparisons[label] = {"baseNs": base, "headNs": head, "headOverBase": head / base,
                              "blocks": blocks, "slots": slots}
    published = read(root / "unprofiled-summary.json")
    require(calls == published["actualSenderOperations"] == 2_949_120, "Unexpected historical call count")
    for item in published["comparisons"]:
        derived = comparisons[item["parameterLabel"]]
        for source, target in (("medianBaseNs", "baseNs"), ("medianHeadNs", "headNs"),
                               ("headBaseRatio", "headOverBase")):
            require(math.isclose(item[source], derived[target], rel_tol=1e-12),
                    "Raw timing does not reproduce the retained summary")
    return {"senderQueuedOperations": calls, "comparisons": comparisons}


def allocation(root):
    verify_identity(root)
    slots = []
    completed = 0
    for index, arm in enumerate(ORDER[:4], 1):
        directory = root / f"{index:02d}-{arm}"
        require((directory / "process-exit-code.txt").read_text().strip() == "0", "Historical allocation failed")
        report = read(directory / "gate.json")
        require(report["passed"] and report["injectedBytesPerOperation"] == 0 and
                report["configuration"] == "Release" and len(report["cases"]) == 1,
                "Unexpected historical allocation mode")
        case = report["cases"][0]
        require(case["passed"] and case["name"] == "rpc-oneway-sharedmemory-c1" and
                case["concurrency"] == 1 and case["warmupOperations"] == 512 and
                case["operationsPerSample"] == 4000 and case["sampleCount"] == len(case["samples"]) == 5 and
                case["maxBytesPerOperation"] == 960 and case["maxSpreadBytesPerOperation"] == 20,
                "Historical allocation workload or policy changed")
        samples = []
        for sample in case["samples"]:
            require(sample["completedOperations"] == 4000, "Historical allocation did not complete")
            measured = sample["allocatedBytes"] / sample["completedOperations"]
            require(math.isclose(measured, sample["bytesPerOperation"], rel_tol=1e-12),
                    "Historical allocation normalization differs")
            samples.append(measured)
            completed += sample["completedOperations"]
        require(math.isclose(median(samples), case["medianBytesPerOperation"], rel_tol=1e-12),
                "Historical allocation median differs")
        slots.append({"slot": directory.name, "arm": arm, "medianBytesPerOperation": median(samples),
                      "samplesBytesPerOperation": samples})
    base = mean(item["medianBytesPerOperation"] for item in slots if item["arm"] == "base")
    head = mean(item["medianBytesPerOperation"] for item in slots if item["arm"] == "head")
    published = read(root / "summary.json")
    require(completed == published["completedMeasuredOperations"] == 80_000 and
            math.isclose(base, published["baseMeanOfSlotMedians"], rel_tol=1e-12) and
            math.isclose(head, published["headMeanOfSlotMedians"], rel_tol=1e-12),
            "Raw allocation does not reproduce the retained summary")
    return {"receiverCompletedOperations": completed, "baseBytesPerOperation": base,
            "headBytesPerOperation": head, "deltaBytesPerOperation": head - base,
            "headOverBase": head / base, "slots": slots}


def profile(path):
    document = read(path)
    names = [item["name"] for item in document["shared"]["frames"]]
    totals = defaultdict(float)
    for thread in document["profiles"]:
        require(thread["type"] == "evented" and thread["unit"] == "milliseconds",
                "Unknown historical profile representation")
        stack = []
        previous = thread["startValue"]
        for event in thread["events"]:
            elapsed = event["at"] - previous
            require(elapsed >= 0, "Profile events are out of order")
            if elapsed > 0 and stack:
                active = [names[index] for index in stack]
                if SENDER_FRAME in active and any(name.startswith("Activity WorkloadActual(") for name in active):
                    totals["actualTaggedSender"] += elapsed
                    admission = any(any(f"SharpLinkClient.{method}(" in name for method in ADMISSION)
                                    for name in active)
                    resolve = any("SharpLinkClient.ResolveCallControlForInvocation(" in name for name in active)
                    if admission:
                        totals["admissionSubtree"] += elapsed
                    if admission or resolve:
                        totals["admissionOrCallControl"] += elapsed
                    if any("RpcSession+SendPump.TryEnqueue(" in name for name in active):
                        totals["sendQueueSubtree"] += elapsed
                        if any("System.Threading.Monitor.Enter_Slowpath(" in name for name in active):
                            totals["sendQueueMonitorSlowpath"] += elapsed
            if event["type"] == "O":
                stack.append(event["frame"])
            else:
                require(event["type"] == "C" and stack and stack[-1] == event["frame"],
                        "Profile contains unmatched stack events")
                stack.pop()
            previous = event["at"]
        require(not stack, "Profile has an unclosed stack")
    require(totals["actualTaggedSender"] > 0, "Profile has no tagged actual sender samples")
    label = re.search(r"\(PayloadSize_ (16|256)\)", path.name)
    require(label is not None, "Unknown historical profile label")
    return {"file": path.name, "parameterLabel": f"PayloadSize={label[1]}",
            "projectedSampleWeightMilliseconds": dict(totals),
            "percentOfActualTaggedSender": {key: value * 100 / totals["actualTaggedSender"]
                                            for key, value in totals.items()}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timing-artifact", type=Path, required=True)
    parser.add_argument("--allocation-artifact", type=Path, required=True)
    parser.add_argument("--timing-zip", type=Path)
    parser.add_argument("--allocation-zip", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    digests = {}
    for kind, path in (("timing", args.timing_zip), ("allocation", args.allocation_zip)):
        if path is not None:
            with path.open("rb") as stream:
                digests[kind] = hashlib.file_digest(stream, "sha256").hexdigest()
            require(digests[kind] == ZIP_DIGESTS[kind], f"{kind} ZIP digest differs from the retained artifact")
    profiles = {}
    for arm in ("base", "head"):
        files = sorted((args.timing_artifact / "diagnostics" / arm).rglob("*.speedscope.json"))
        require(len(files) == 2, "Expected two diagnostic profiles per arm")
        profiles[arm] = [profile(path) for path in files]
    report = {"schemaVersion": 1, "base": BASE, "head": HEAD, "verifiedZipSha256": digests,
              "timing": timing(args.timing_artifact), "allocation": allocation(args.allocation_artifact),
              "profiles": profiles,
              "profileLimit": "Weights are the CPU sampling projection of synchronous sender stacks tagged WorkloadActual. They are not lock wait durations, request tails, whole-process CPU or a causal attribution."}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Verified {report['timing']['senderQueuedOperations']} sender-queued calls, "
          f"{report['allocation']['receiverCompletedOperations']} receiver-completed allocation calls and four CPU profiles.")


if __name__ == "__main__":
    main()
