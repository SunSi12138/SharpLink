#!/usr/bin/env python3
"""Audit dedicated-sender diagnostics. Never equate them to canonical RPC/BDN scores."""
import argparse
import json
import math
from pathlib import Path
from statistics import median
from summarize import paired, require

A = "1a54993d3550911f5979a7b4d3f9f5f63ee86a64"
B = "2d98242c66f43347dd1b7413a47fadc86cc88bda"
COMMITS = {"A": A, "B": B}
ORDER = "ABBABAAB"
CASES = ("tcp-c1-p1", "tcp-c8-p4", "sharedmemory-c1-p1", "sharedmemory-c8-p4")
BATCHES, BATCH, WARMUP = 256, 4096, 32


def read(path):
    return json.loads(path.read_text())


def validate(value, arm, case):
    require(value["Schema"] == 1 and value["Passed"] is True and value["DiagnosticOnly"] is True, "Invalid diagnostic report")
    require(value["SourceSha"] == COMMITS[arm], "Wrong source")
    require(value["Magic"] == (137 if arm == "A" else 183), "Wrong protocol dialect")
    require(value["RoutePrefixBytes"] == 16 and value["ProcessorCount"] == 2, "Prefix or CPU changed")
    c, pool = value["Concurrency"], value["Pool"]
    require(case == f"{value['Transport']}-c{c}-p{pool}" and (c, pool) in ((1, 1), (8, 4)), "Wrong scenario")
    require((value["BatchSize"], value["Batches"], value["WarmupBatches"]) == (BATCH, BATCHES, WARMUP), "Workload changed")
    require(value["Operations"] == BATCH * BATCHES and value["Warmup"] == BATCH * WARMUP, "Wrong operation count")
    require(value["ReceiverCompleted"] == value["UniqueReceived"] == BATCH * (BATCHES + WARMUP), "Incomplete/duplicate receive")
    require(value["BindBefore"] == value["BindAfter"] == (pool if arm == "B" else 0), "Rebind or incomplete preparation")
    require(value["FinalReservations"] == 0, "Leaked reservations")
    m, f = value["Measurement"], value["StopwatchFrequency"]
    require(f > 0 and m["Start"] < m["End"], "Invalid clock")
    require(len(m["Samples"]) == BATCHES*c and len(m["Rounds"]) == BATCHES, "Missing samples")
    threads, cpu, wall, async_calls, queues = {}, [], [], [], []
    last = m["Start"]
    drains = []
    for i, round_ in enumerate(m["Rounds"]):
        require(round_["Round"] == i and last <= round_["Start"] <= round_["SendEnd"] <= round_["DrainEnd"] <= m["End"], "Overlapping/swapped batches")
        last = round_["DrainEnd"]
        require(0 <= round_["QueuedBytes"] <= pool * 8 * 1024 * 1024, "Invalid queue gauge")
        queues.append(round_["QueuedBytes"])
        drains.append((round_["DrainEnd"]-round_["SendEnd"])*1e6/f)
        for j in range(c):
            s = m["Samples"][i*c+j]
            require((s["Round"], s["Worker"], s["Completed"]) == (i, j, BATCH//c), "Missing/duplicate sender sample")
            require(round_["Start"] <= s["Start"] <= s["End"] <= round_["SendEnd"], "Sender outside batch")
            require(threads.setdefault(j, s["Thread"]) == s["Thread"], "Migrated sender thread")
            d = s["CpuEnd"] - s["CpuStart"]
            require(d >= 0 and d <= (s["End"]-s["Start"])*1e9/f+100000, "Invalid sender CPU")
            require(0 <= s["AsyncCalls"] <= s["Completed"], "Invalid asynchronous count")
            require(0 <= s["QueuedBytes"] <= pool*8*1024*1024, "Invalid sampled queue")
            cpu.append(d)
            wall.append((s["End"]-s["Start"])*1e9/f)
            async_calls.append(s["AsyncCalls"])
            queues.append(s["QueuedBytes"])
    require(len(set(threads.values())) == c, "Sender threads are not distinct")
    total_cpu = m["ProcessCpuEnd"]-m["ProcessCpuStart"]
    require(total_cpu >= sum(cpu) > 0 and sum(cpu) == m["SenderCpuNanoseconds"], "CPU accounting mismatch")
    require(m["OtherProcessCpuNanoseconds"] == total_cpu-sum(cpu), "Other CPU is not conserved")
    require(m["AllocatedEnd"] >= m["AllocatedStart"], "Invalid allocation window")
    operations = BATCH*BATCHES
    metrics = {"senderCpuNsPerCall": sum(cpu)/operations, "senderWallNsPerCall": sum(wall)/operations,
               "processCpuNsPerCall": total_cpu/operations, "otherProcessCpuNsPerCall": (total_cpu-sum(cpu))/operations,
               "diagnosticBusinessOpsPerSecond": operations*f/(m["End"]-m["Start"]),
               "asyncCalls": sum(async_calls), "sampledMaxQueueBytes": max(queues),
               "postSendQueueMedianBytes": median(r["QueuedBytes"] for r in m["Rounds"]),
               "drainP50Us": median(drains), "drainP99Us": sorted(drains)[math.ceil(.99*len(drains))-1],
               "diagnosticAllocationBytesPerCall": (m["AllocatedEnd"]-m["AllocatedStart"])/operations,
               "controlBytes": value["ControlBytes"]}
    require([s["Name"] for s in value["Micro"]] == ["control", "route-admission-release"], "Changed microdiagnostics")
    for s in value["Micro"]:
        require(s["Operations"] == 1000000 and s["Start"] < s["End"] and s["CpuStart"] < s["CpuEnd"], "Invalid micro sample")
        metrics[s["Name"]+"CpuNsPerOp"] = (s["CpuEnd"]-s["CpuStart"])/s["Operations"]
    require(all(math.isfinite(x) for x in metrics.values()), "Nonfinite metric")
    return metrics


def report(root):
    manifest = read(root/"manifest.json")
    require(manifest["commits"] == COMMITS and manifest["order"] == ORDER, "Wrong plan")
    results = {case: [] for case in CASES}
    last_end = 0
    runtimes = set()
    for i, arm in enumerate(ORDER, 1):
        for case in CASES:
            folder = root/"slots"/f"{i:02d}-{arm}"/case
            command = read(folder/"command.json")
            require(command["exitCode"] == 0 and command["startedUnixSeconds"] >= last_end, "Failed/overlapping execution")
            last_end = command["completedUnixSeconds"]
            env = command["environment"]
            require(env["SHARPLINK_BENCHMARK_SHA"] == COMMITS[arm] and env["DOTNET_PROCESSOR_COUNT"] == "2", "Command identity mismatch")
            require(env["DOTNET_TieredCompilation"] == env["DOTNET_TieredPGO"] == "1", "Tiering changed")
            require(command["argv"][:3] == ["taskset", "-c", ",".join(map(str, manifest["cpus"]))], "Affinity changed")
            value = read(folder/"result.json")
            runtimes.add(value["Runtime"])
            results[case].append(validate(value, arm, case))
    require(len(runtimes) == 1, "Runtime mismatch")
    summary = {"diagnosticOnly": True, "stage2Authorized": False, "commits": COMMITS, "cases": {}}
    lines = ["# Client sender diagnosis", "", "Dedicated-thread closed-loop diagnostics; not canonical BDN or performance acceptance.", "",
             "| Case | sender CPU A/B ns | B/A | process CPU B/A | other CPU B/A | control B/A | route B/A |", "|---|---:|---:|---:|---:|---:|---:|"]
    for case, values in results.items():
        metrics = {}
        for key in values[0]:
            seq = [v[key] for v in values]
            a = [x for x, label in zip(seq, ORDER) if label == "A"]
            b = [x for x, label in zip(seq, ORDER) if label == "B"]
            if min(seq) > 0:
                metrics[key] = paired({"base": a, "head": b})
            else:
                metrics[key] = {"baseMedian": median(a), "headMedian": median(b), "headOverBase": None}
            metrics[key]["orderedSamples"] = seq
        summary["cases"][case] = metrics
        s = metrics["senderCpuNsPerCall"]
        lines.append(f"| {case} | {s['baseMedian']:.1f}/{s['headMedian']:.1f} | {s['headOverBase']:.4f} | {metrics['processCpuNsPerCall']['headOverBase']:.4f} | {metrics['otherProcessCpuNsPerCall']['headOverBase']:.4f} | {metrics['controlCpuNsPerOp']['headOverBase']:.4f} | {metrics['route-admission-releaseCpuNsPerOp']['headOverBase']:.4f} |")
    lines += ["", "All samples retained. Four pairs; exploratory log-t intervals in JSON, no equivalence margin or multiple-testing correction.",
              "Sender CPU is time inside dedicated send loops, not all client CPU. Other process CPU includes runtime/transport/service/harness/synchronization, NOT pure server CPU.",
              "Queue values are gauges at sender/batch boundaries, not maxima over time. Local service completion fences add no wire receipt.",
              "Control/route microloops differ from actual invocation inlining and must not be subtracted from RPC costs."]
    (root/"summary.json").write_text(json.dumps(summary, indent=2)+"\n")
    (root/"summary.md").write_text("\n".join(lines)+"\n")
    return summary


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    report(parser.parse_args().root)
