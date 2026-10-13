#!/usr/bin/env python3
"""Classify CPU stack projections with explicit sender and whole-capture server scopes."""

import argparse
from collections import defaultdict
import json
from pathlib import Path
import re

from summarize import read, require

CATEGORIES = {
    "clientSendCallInclusive": ("SharpLinkClient.SendRpcCall(",),
    "clientRoute": ("ClientContractRouteTable.TryGetRoute(", "ClientContractRoute.SelectConnection(",
                    "SharpLinkClient.GetReadyConnection(", "SharpLinkClient.ResolveCallControlForInvocation("),
    "identityWriteOrValidation": ("ProtocolV2RequestIdentityCodec.Write(", "ProtocolV2RequestIdentityCodec.Validate("),
    "identityRead": ("ProtocolV2RequestIdentityCodec.Read(", "SharpLinkServer.ReadRequestIdentity("),
    "serverContractAdmission": ("ServerContractAdmission.TryAcquire(", "ServerContractAdmission.Validate("),
    "sendQueueInclusive": ("RpcSession+SendPump.TryEnqueue(",),
    "sendQueueMonitorSlowpath": (),
}


def classify(path):
    document = read(path)
    names = [frame["name"] for frame in document["shared"]["frames"]]
    totals = {scope: defaultdict(float) for scope in ("actualTaggedSender", "wholeCaptureServerStacks")}
    leaves = {scope: defaultdict(float) for scope in totals}
    for thread in document["profiles"]:
        require(thread["type"] == "evented" and thread["unit"] == "milliseconds", "Unexpected profile representation")
        stack = []
        previous = thread["startValue"]
        for event in thread["events"]:
            elapsed = event["at"] - previous
            require(elapsed >= 0, "Unordered profile events")
            if elapsed > 0 and stack:
                active = [names[index] for index in stack]
                scopes = []
                if any("SharpLink.Benchmarks.UnaryBenchmarks.Rpc_OnewayPublish()" in name for name in active) and any(
                        name.startswith("Activity WorkloadActual(") for name in active):
                    scopes.append("actualTaggedSender")
                if any("!SharpLink.Server." in name for name in active):
                    scopes.append("wholeCaptureServerStacks")
                leaf = next((name for name in reversed(active) if "!" in name), "unresolved")
                for scope in scopes:
                    totals[scope]["sampleWeightMilliseconds"] += elapsed
                    leaves[scope][leaf] += elapsed
                    for category, matches in CATEGORIES.items():
                        matched = any(any(pattern in name for pattern in matches) for name in active)
                        if category == "sendQueueMonitorSlowpath":
                            matched = any("RpcSession+SendPump.TryEnqueue(" in name for name in active) and any(
                                "System.Threading.Monitor.Enter_Slowpath(" in name for name in active)
                        if matched:
                            totals[scope][category] += elapsed
            if event["type"] == "O":
                stack.append(event["frame"])
            else:
                require(event["type"] == "C" and stack and stack[-1] == event["frame"], "Unmatched profile stack")
                stack.pop()
            previous = event["at"]
        require(not stack, "Unclosed profile stack")
    require(totals["actualTaggedSender"]["sampleWeightMilliseconds"] > 0, "No actual-tagged sender samples")
    require(totals["wholeCaptureServerStacks"]["sampleWeightMilliseconds"] > 0, "No server-frame samples")
    label = re.search(r"\(PayloadSize_ (16|256)\)", path.name)
    require(label is not None, "Unexpected CPU profile label")
    scopes = {}
    for scope, metrics in totals.items():
        weight = metrics["sampleWeightMilliseconds"]
        scopes[scope] = {
            "sampleWeightMilliseconds": weight,
            "categoriesPercent": {key: metrics[key] * 100 / weight for key in CATEGORIES},
            "topLeafFrames": [{"frame": name, "sampleWeightMilliseconds": elapsed,
                               "percent": elapsed * 100 / weight}
                              for name, elapsed in sorted(leaves[scope].items(), key=lambda item: -item[1])[:25]],
        }
    return {"file": path.name, "parameterLabel": f"PayloadSize={label[1]}", "scopes": scopes}


def summarize_profiles(root):
    provenance = read(root / "provenance.json")
    rows = []
    for index, arm in enumerate(provenance["order"], 1):
        slot = root / "profiles" / f"{index:02d}-{arm}"
        require(read(slot / "command.json")["exitCode"] == 0, f"Failed profiler: {slot.name}")
        files = sorted((slot / "bdn").rglob("*.speedscope.json"))
        traces = list((slot / "bdn").rglob("*.nettrace"))
        require(len(files) == len(traces) == 2, f"Missing raw EventPipe evidence: {slot.name}")
        profiles = [classify(path) for path in files]
        require({item["parameterLabel"] for item in profiles} == {"PayloadSize=16", "PayloadSize=256"},
                "CPU profiles have unexpected or duplicate labels")
        for item in profiles:
            rows.append({"slot": slot.name, "arm": arm, **item})
    report = {
        "schemaVersion": 1, "commits": provenance["commits"], "rows": rows,
        "classificationPatterns": CATEGORIES,
        "limits": [
            "Sender scope requires both the synchronous benchmark frame and a WorkloadActual activity tag.",
            "Server scope includes all captured server-frame stacks, including warmups and dispatch drain; it is not phase-matched with the sender denominator.",
            "Category subtrees can overlap; percentages must not be added together.",
            "Weights are CPU-sampling projections, not lock wait duration, request tails or proof of causation. Inlining and unresolved frames can hide individual helpers.",
            "Profiled and disassembly timings are excluded from unprofiled performance acceptance.",
        ],
    }
    (root / "profile-summary.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    lines = ["# RPC contract routing CPU attribution", "",
             f"Base `{provenance['commits']['base']}`; head `{provenance['commits']['head']}`.", "",
             "## Actual-tagged synchronous sender", "",
             "Percentages use only actual-tagged synchronous sender stack weights. Inclusive subtrees overlap.", "",
             "| Slot | Label | Projected sample ms | Send-call subtree % | Route subtree % | Identity write/validate % | Queue slowpath % |",
             "|---|---|---:|---:|---:|---:|---:|"]
    for row in rows:
        scope = row["scopes"]["actualTaggedSender"]
        values = scope["categoriesPercent"]
        lines.append(f"| {row['slot']} | {row['parameterLabel']} | {scope['sampleWeightMilliseconds']:.3f} | {values['clientSendCallInclusive']:.2f} | {values['clientRoute']:.2f} | {values['identityWriteOrValidation']:.2f} | {values['sendQueueMonitorSlowpath']:.2f} |")
    lines += ["", "## Whole-capture server-frame stacks", "",
              "This denominator includes warmup and dispatch-drain samples. It is retained separately from actual-tagged sender measurements and cannot be used as phase-matched server cost per request.", "",
              "| Slot | Label | Projected sample ms | Identity read % | Contract admission % |",
              "|---|---|---:|---:|---:|"]
    for row in rows:
        scope = row["scopes"]["wholeCaptureServerStacks"]
        values = scope["categoriesPercent"]
        lines.append(f"| {row['slot']} | {row['parameterLabel']} | {scope['sampleWeightMilliseconds']:.3f} | {values['identityRead']:.2f} | {values['serverContractAdmission']:.2f} |")
    lines += ["", *[f"- {item}" for item in report["limits"]], "",
              "Top leaf stacks and exact classification patterns are retained in JSON. Original nettrace and speedscope files, source hashes, process exits and independent JIT output remain in the artifact.", ""]
    (root / "profile-summary.md").write_text("\n".join(lines), encoding="utf-8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    print(json.dumps(summarize_profiles(args.root), indent=2))
