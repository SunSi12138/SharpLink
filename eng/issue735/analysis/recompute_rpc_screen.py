#!/usr/bin/env python3
"""Recompute one fixed-source RPC screen; never merge measurement versions.

A process, not an individual RPC, is an independent A/B repeat. Failed and
unrun positions come from the predeclared plan and are never synthesized into
measurements. This validator understands the pinned architecture-screen recipe
(both transports, two AA rounds, five-second/minimum-two-operation warmup).
It deliberately does not emit production Go.
"""
import argparse
from bisect import bisect_right
import csv
import hashlib
import json
import math
from pathlib import Path
import statistics

METRICS = [
    "throughputRpcPerSecond", "throughputLogicalItemsPerSecond",
    "cpuMicrosecondsPerRpc", "allocationBytesPerRpc",
    "settledThroughputRpcPerSecond", "settledCpuMicrosecondsPerRpc",
    "settledAllocationBytesPerRpc", "p50Microseconds", "p95Microseconds",
    "p99Microseconds", "p999Microseconds", "threadPoolCompletedWorkItemsPerRpc",
    "gen0Collections", "gen1Collections", "gen2Collections", "gcPauseTotalMicroseconds",
]
SETTLED_FIELDS = [
    "settledElapsedSeconds", "settledThroughputRpcPerSecond",
    "settledThroughputLogicalItemsPerSecond", "settledCpuTotalMicroseconds",
    "settledCpuMicrosecondsPerRpc", "settledAllocationBytesTotal",
    "settledAllocationBytesPerRpc", "settledGen0Collections",
    "settledGen1Collections", "settledGen2Collections",
    "settledGcPauseTotalMicroseconds", "completedToSettledMicroseconds",
    "completedBoundaryDefinition", "settledBoundaryDefinition",
    "settledObserverCosts", "settledExcludedWork",
]
# Configuration, not outcomes: exclude actual thread counts and session IDs.
ENVIRONMENT_FIELDS = [
    "runtime", "runtimeVersion", "os", "architecture", "processorCount", "machine",
    "serverGc", "gcLatencyMode", "stopwatchFrequency",
    "threadPoolOriginalWorkerMinimum", "threadPoolOriginalIoMinimum",
    "threadPoolConfiguredWorkerMinimum", "threadPoolWorkerMaximum", "threadPoolIoMaximum",
    "env.DOTNET_TC_QuickJitForLoops", "env.COMPlus_TC_QuickJitForLoops",
    "env.DOTNET_gcServer", "env.DOTNET_GCHeapCount", "env.DOTNET_GCConserveMemory",
    "env.DOTNET_ThreadPool_UnfairSemaphoreSpinLimit",
    "env.GITHUB_RUN_ID", "env.GITHUB_RUN_ATTEMPT", "env.GITHUB_SHA",
]
TRANSPORTS = ["tcp", "sharedmemory"]
AA_ROUNDS = 2
WARMUP_OPS = 2
MINIMUM_WARMUP_SECONDS = 5


def parse_report(path):
    result = {}
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        if key in result:
            raise ValueError(f"Duplicate report field: {path.name}:{key}")
        result[key] = value
    return result


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def summarize(values):
    return dict(n=len(values), median=statistics.median(values) if values else None,
                minimum=min(values) if values else None, maximum=max(values) if values else None,
                raw=values)


def expect(values, expected, context):
    for key, value in expected.items():
        if values.get(key) != str(value):
            raise ValueError(f"{context}: {key}: expected {value!r}, got {values.get(key)!r}")


def number(values, key):
    value = float(values[key])
    if not math.isfinite(value) or value < 0:
        raise ValueError("Non-finite or negative metric: " + key)
    return value


def close(actual, expected, context, absolute=1e-6):
    if not math.isclose(actual, expected, rel_tol=1e-10, abs_tol=absolute):
        raise ValueError(f"Numeric mismatch: {context}: {actual!r} != {expected!r}")


def checked_cases(cases):
    result = []
    seen = set()
    for case in cases:
        if len(case) != 5:
            raise ValueError("A case must contain shape/items/bytes/concurrency/operations")
        shape, items, size, concurrency, operations = case
        if shape not in {"Unary", "Server", "Client", "Duplex"} or any(
                type(value) is not int for value in case[1:]):
            raise ValueError("Invalid case: " + str(case))
        if items < 0 or (shape == "Unary" and items != 1) or min(size, concurrency, operations) <= 0:
            raise ValueError("Invalid case dimensions: " + str(case))
        key = f"{shape}-{items}x{size}-c{concurrency}"
        if key in seen:
            raise ValueError("Duplicate planned case: " + key)
        seen.add(key)
        result.append((key, dict(shape=shape, itemsPerRpc=items, payloadBytes=size,
                                 concurrency=concurrency, measuredOpsPerWorker=operations)))
    return result


def expand_plan(plan):
    """Expand the actual collector recipe, including its reversed second round."""
    cases, aa_cases = checked_cases(plan["cases"]), checked_cases(plan["aaCases"])
    orders = plan["abOrders"]
    if not cases or not orders:
        raise ValueError("The plan must contain cases and AB orders")
    planned = {}

    def add(label, kind, cell, arm, round_index, position, workload):
        if label in planned:
            raise ValueError("Duplicate planned process: " + label)
        planned[label] = dict(kind=kind, cell=cell, arm=arm, round=round_index,
                              position=position, workload=workload)

    for round_index, order in enumerate(orders):
        if not order or len(order) % 2 or any(
                set(order[i:i + 2]) != {"A", "B"} for i in range(0, len(order), 2)):
            raise ValueError("Every predeclared adjacent pair must contain one A and one B")
        transports = TRANSPORTS if round_index == 0 else list(reversed(TRANSPORTS))
        for transport in transports:
            for key, dimensions in cases if round_index == 0 else reversed(cases):
                cell = transport + "-" + key
                workload = dict(dimensions, transport=transport)
                for position, arm in enumerate(order):
                    add(f"ab-r{round_index}-{cell}-p{position}-{arm}", "ab", cell,
                        arm, round_index, position, workload)
    for transport in TRANSPORTS:
        for key, dimensions in aa_cases:
            cell = transport + "-" + key
            for round_index in range(AA_ROUNDS):
                for side in ["left", "right"] if round_index == 0 else ["right", "left"]:
                    add(f"aa-r{round_index}-{cell}-{side}", "aa", cell, "A", round_index,
                        side, dict(dimensions, transport=transport))
    return planned


def validate_identities(root, plan):
    identities = plan["sourceTrees"]
    if set(identities) != {"A", "B"}:
        raise ValueError("Both compiled A/B source identities are required")
    for key in ["MeasurementSourceSha", "SendPumpSourceSha256"]:
        if identities["A"][key] != identities["B"][key]:
            raise ValueError("A/B source mismatch: " + key)
    if plan["runtimePopulation"] not in {"jit-pgo0", "jit-pgo1", "nativeaot"}:
        raise ValueError("Unknown runtime population")
    cpus = plan["cpuAffinity"]
    if len(cpus) != 4 or len(set(cpus)) != 4 or any(type(cpu) is not int or cpu < 0 for cpu in cpus):
        raise ValueError("The pinned screen recipe requires four distinct CPUs")
    aot = plan["runtimePopulation"] == "nativeaot"
    binary_name = "SharpLink.Benchmarks" if aot else "SharpLink.Benchmarks.dll"
    hashes = {}
    for arm, identity in identities.items():
        if identity["BaselineSha"] != plan["baselineSha"] or identity["CandidateSha"] != (
                plan["baselineSha"] if arm == "A" else plan["candidateSha"]):
            raise ValueError("Plan/source commit mismatch: " + arm)
        for key in ["BaselineSha", "CandidateSha", "RuntimeSourceTreeSha", "MeasurementSourceSha",
                    "ControllerSourceSha256", "SendPumpSourceSha256", "binarySha256"]:
            value = identity[key]
            if len(value) not in {40, 64} or any(character not in "0123456789abcdefABCDEF" for character in value):
                raise ValueError("Invalid source identity SHA: " + arm + ":" + key)
        if not identity["Mode"] or identity["productionScheduler"] != "dev-SendPump" or identity["b3ReadyWriterEnabled"] is not False:
            raise ValueError("Unrecognized production scheduler identity: " + arm)
        binary = root / "binaries" / arm / binary_name
        if digest(binary) != identity["binarySha256"]:
            raise ValueError("Binary hash mismatch: " + arm)
        hashes[arm] = dict(measuredBinarySha256=identity["binarySha256"])
        if not aot:
            hashes[arm]["runtimeAssemblySha256"] = digest(root / "binaries" / arm / "SharpLink.Runtime.dll")
    return hashes


def validate_command(row, planned, plan):
    command = row["command"]
    aot = plan["runtimePopulation"] == "nativeaot"
    binary_position = 3 if aot else 4
    prefix_length = binary_position + 1
    if not isinstance(command, list) or len(command) != prefix_length + 11 or any(
            not isinstance(value, str) for value in command):
        raise ValueError("Invalid recorded RPC command: " + row["name"])
    if command[:3] != ["taskset", "-c", ",".join(map(str, plan["cpuAffinity"]))]:
        raise ValueError("CPU affinity differs from plan: " + row["name"])
    binary = Path(command[binary_position])
    if binary.name != ("SharpLink.Benchmarks" if aot else "SharpLink.Benchmarks.dll") or binary.parent.name != planned["arm"]:
        raise ValueError("Command binary/arm mismatch: " + row["name"])
    if not aot and Path(command[3]).name != "dotnet":
        raise ValueError("JIT command does not invoke dotnet")
    identity = plan["sourceTrees"][planned["arm"]]
    work = planned["workload"]
    expected = [work["transport"], work["shape"], str(work["itemsPerRpc"]), str(work["payloadBytes"]),
                str(work["concurrency"]), str(WARMUP_OPS), str(work["measuredOpsPerWorker"]),
                plan["baselineSha"], identity["RuntimeSourceTreeSha"], identity["Mode"]]
    if command[prefix_length:-1] != expected or Path(command[-1]).name != row["name"] + ".txt":
        raise ValueError("RPC command workload/identity differs from plan: " + row["name"])


def validate_report(report, planned, plan, hashes, label):
    aot = plan["runtimePopulation"] == "nativeaot"
    identity = plan["sourceTrees"][planned["arm"]]
    work = planned["workload"]
    expected = {
        "schema": "sharplink-flow-architecture-evidence-v1", "status": "passed",
        "candidateSha": identity["CandidateSha"], "baselineSha": plan["baselineSha"],
        "runtimeSourceTreeSha": identity["RuntimeSourceTreeSha"],
        "measurementSourceSha": identity["MeasurementSourceSha"],
        "controllerSourceSha256": identity["ControllerSourceSha256"],
        "sendPumpSourceSha256": identity["SendPumpSourceSha256"], "compiledMode": identity["Mode"],
        "productionScheduler": "dev-SendPump", "b3ReadyWriterEnabled": "false",
        "samePhysicalSessionBeforeAndAfter": "true", "builtForNativeAot": str(aot).lower(),
        "dynamicCodeSupported": str(not aot).lower(), "dynamicCodeCompiled": str(not aot).lower(),
        "processorCount": len(plan["cpuAffinity"]), "warmupOpsPerWorker": WARMUP_OPS,
        "minimumWarmupSecondsPerWorker": MINIMUM_WARMUP_SECONDS,
        "warmupPolicy": "each-worker-runs-until-fixed-minimum-ops-and-minimum-duration-both-met",
        "successfulRpcs": work["concurrency"] * work["measuredOpsPerWorker"], "failedRpcs": 0,
        "threadPoolConfiguredWorkerMinimum": max(32, len(plan["cpuAffinity"]) * 4),
        "env.DOTNET_PROCESSOR_COUNT": len(plan["cpuAffinity"]), "env.DOTNET_ReadyToRun": "0",
        "env.DOTNET_TieredCompilation": "<unset>" if aot else "1",
        "env.DOTNET_TieredPGO": "<unset>" if aot else "0" if plan["runtimePopulation"] == "jit-pgo0" else "1",
        "env.GITHUB_SHA": plan["measurementRecipeSha"],
        "generatedContract": "SharpLink.Benchmarks.IBenchmarkRpc",
        "validation": "full-payload-both-directions-and-exact-stream-item-count",
        "requestTimeout": "disabled-as-in-existing-BenchmarkEnvironment",
        "collectorTimeoutSeconds": "60-external-not-enforced-by-production-options",
        "heartbeat": "5min-period-10min-timeout-as-in-existing-BenchmarkEnvironment" if work["transport"] == "sharedmemory" else "transport-default",
        "protocol": "v2-default-unmodified", "flushPolicy": "profile-default-no-override",
        "compression": "default-no-providers", "performanceProfile": "Balanced",
        "actualClientStreamReceiveWindowBytes": 1048576, "actualClientConnectionReceiveWindowBytes": 16777216,
        "actualClientMaxSendQueueBytes": 8388608, "actualClientMaxPreCreditSerializedBytes": 4194304,
        "measurementScope": "same-process-generated-rpc-client-plus-server-plus-validation",
        "latencyDefinition": "individual-rpc-start-to-validated-completion-no-batch-latency",
        "workloadModel": "closed-loop-one-inflight-rpc-per-persistent-worker",
        "quantileMethod": "nearest-rank", "physicalReadyConnectionsAfter": 1,
        "pendingCallsAfterSettlement": 0, "activeClientCallsAfterSettlement": 0,
        "activeClientStreamsAfterSettlement": 0, "activeServerCallsAfterSettlement": 0,
        "fullAcceptanceGate": "false-stage1-throughput-evidence-only",
    }
    expected.update(work)
    expected.update(hashes[planned["arm"]])
    expect(report, expected, "Report differs from plan/recipe: " + label)
    for suffix in ["TieredPGO", "TieredCompilation", "ReadyToRun"]:
        actual = report.get("env.COMPlus_" + suffix)
        if actual not in {"<unset>", expected["env.DOTNET_" + suffix]}:
            raise ValueError("Conflicting/unrecorded COMPlus runtime flag: " + label + ":" + suffix)
    present = [key in report for key in SETTLED_FIELDS]
    if any(present) and not all(present):
        raise ValueError("Partial settled measurement fields: " + label)
    settled = all(present)
    count = int(report["successfulRpcs"])
    seconds = number(report, "elapsedSeconds")
    if seconds == 0:
        raise ValueError("Zero measurement interval: " + label)
    close(number(report, "throughputRpcPerSecond"), count / seconds, label + ":throughput")
    close(number(report, "cpuMicrosecondsPerRpc"), number(report, "cpuTotalMicroseconds") / count, label + ":CPU")
    close(number(report, "allocationBytesPerRpc"), number(report, "allocationBytesTotal") / count, label + ":allocation")
    logical = count * work["itemsPerRpc"]
    stream_items = (0 if work["shape"] == "Unary" else 2 if work["shape"] == "Duplex" else 1) * work["itemsPerRpc"]
    expect(report, {"logicalItems": logical, "expectedStreamPayloadItemsPerRpc": stream_items,
                    "expectedApplicationPayloadBytesPerRpc": (2 if work["shape"] == "Unary" else stream_items) * work["payloadBytes"]}, label)
    close(number(report, "throughputLogicalItemsPerSecond"), logical / seconds, label + ":items")
    close(number(report, "throughputStreamPayloadItemsPerSecond"), count * stream_items / seconds, label + ":stream-items")
    for metric in METRICS:
        if metric.startswith("settled") and not settled:
            continue
        number(report, metric)
    if settled:
        expect(report, {
            "completedBoundaryDefinition": "all-measured-rpcs-validated-before-lifecycle-settlement",
            "settledBoundaryDefinition": "measured-rpcs-plus-observed-call-stream-settlement",
            "settledObserverCosts": "includes-WaitForIdle-polling-and-intermediate-completed-counter-observation",
            "settledExcludedWork": "raw-sample-file-IO-session-probe-report-formatting-and-transport-disposal",
        }, label + ":settled-boundary")
        settled_seconds = number(report, "settledElapsedSeconds")
        if settled_seconds < seconds or number(report, "settledCpuTotalMicroseconds") < number(report, "cpuTotalMicroseconds") or number(report, "settledAllocationBytesTotal") < number(report, "allocationBytesTotal"):
            raise ValueError("Settled counters precede completed counters: " + label)
        close(number(report, "settledThroughputRpcPerSecond"), count / settled_seconds, label + ":settled-throughput")
        close(number(report, "settledCpuMicrosecondsPerRpc"), number(report, "settledCpuTotalMicroseconds") / count, label + ":settled-CPU")
        close(number(report, "settledAllocationBytesPerRpc"), number(report, "settledAllocationBytesTotal") / count, label + ":settled-allocation")
        close(number(report, "completedToSettledMicroseconds"), (settled_seconds - seconds) * 1e6, label + ":settled-elapsed")
        for key in ["settledGen0Collections", "settledGen1Collections", "settledGen2Collections", "settledGcPauseTotalMicroseconds"]:
            number(report, key)
    return settled


def validate_samples(path, report):
    samples_path = path.with_name(path.name + ".samples.csv")
    workers_path = path.with_name(path.name + ".workers.csv")
    fairness_path = path.with_name(path.name + ".fairness.csv")
    concurrency, operations = int(report["concurrency"]), int(report["measuredOpsPerWorker"])
    count, frequency = int(report["successfulRpcs"]), int(report["stopwatchFrequency"])
    if frequency <= 0:
        raise ValueError("Invalid stopwatch frequency")
    counts, last_ends = [0] * concurrency, [0] * concurrency
    ends_us = [[] for _ in range(concurrency)]
    latencies = []
    with samples_path.open(encoding="utf-8-sig") as stream:
        for sample in csv.DictReader(stream):
            worker, operation = int(sample["worker"]), int(sample["operation"])
            if not 0 <= worker < concurrency or operation != counts[worker] or operation >= operations:
                raise ValueError("Duplicate/missing/out-of-order worker operation: " + path.name)
            start, end, ticks = (int(sample[key]) for key in ["start_timestamp", "end_timestamp", "latency_ticks"])
            if start < last_ends[worker] or end < start or ticks != end - start:
                raise ValueError("Invalid/sequencing-violating raw timestamp: " + path.name)
            latency = number(sample, "latency_us")
            close(latency, ticks * 1e6 / frequency, path.name + ":raw-latency")
            start_us, end_us = number(sample, "start_us_from_gate"), number(sample, "end_us_from_gate")
            close(end_us - start_us, latency, path.name + ":relative-latency")
            latencies.append(latency)
            ends_us[worker].append(end_us)
            counts[worker] += 1
            last_ends[worker] = end
    if len(latencies) != count or counts != [operations] * concurrency:
        raise ValueError("Individual RPC/worker sample count mismatch: " + path.name)
    latencies.sort()
    for key, fraction in [("p50Microseconds", .5), ("p95Microseconds", .95),
                          ("p99Microseconds", .99), ("p999Microseconds", .999)]:
        close(float(report[key]), latencies[max(0, math.ceil(count * fraction) - 1)], path.name + ":" + key)
    with workers_path.open(encoding="utf-8-sig") as stream:
        workers = list(csv.DictReader(stream))
    if len(workers) != concurrency or sorted(int(worker["worker"]) for worker in workers) != list(range(concurrency)):
        raise ValueError("Duplicate/missing worker summary: " + path.name)
    for worker in workers:
        expect(worker, {"completed": operations, "expected": operations, "failed": "false"}, path.name + ":worker")
        if int(worker["warmup_completed"]) < WARMUP_OPS or number(worker, "warmup_seconds") < MINIMUM_WARMUP_SECONDS:
            raise ValueError("Worker warmup is below the recipe: " + path.name)
    if sum(int(worker["warmup_completed"]) for worker in workers) != int(report["actualWarmupRpcs"]):
        raise ValueError("Warmup RPC count mismatch: " + path.name)
    with fairness_path.open(encoding="utf-8-sig") as stream:
        fairness = list(csv.DictReader(stream))
    if [float(row["fraction"]) for row in fairness] != [.1, .25, .5, .75, .9, 1.0]:
        raise ValueError("Unexpected fairness observation fractions: " + path.name)
    jain = []
    for row in fairness:
        fraction, elapsed_us = number(row, "fraction"), number(row, "elapsed_us")
        close(elapsed_us, float(report["elapsedSeconds"]) * 1e6 * fraction,
              path.name + ":fairness-time", absolute=1e6 / frequency + 1e-6)
        completed = [bisect_right(ends, elapsed_us) for ends in ends_us]
        if [int(value) for value in row["per_worker_completions"].split(";")] != completed:
            raise ValueError("Fairness completion counts disagree with raw timestamps: " + path.name)
        expect(row, {"min_completions": min(completed), "max_completions": max(completed),
                     "total_completions": sum(completed)}, path.name + ":fairness-counts")
        squares = sum(value * value for value in completed)
        if not squares:
            expect(row, {"jain_index": "N/A"}, path.name + ":fairness-zero")
        else:
            observed = number(row, "jain_index")
            close(observed, sum(completed) ** 2 / (concurrency * squares), path.name + ":fairness-Jain")
            if 0 < fraction < 1:
                jain.append(observed)
    return dict(reportSha256=digest(path), rawSampleSha256=digest(samples_path),
                workersSha256=digest(workers_path), fairnessSha256=digest(fairness_path),
                successfulRpcs=count, individualQuantilesVerified=True, workerSamplesVerified=True,
                fairnessRecomputedFromIndividualSamples=True,
                minimumObservedJain=min(jain) if jain else None)


def coverage(labels, process_by_label):
    states = {label: process_by_label[label]["status"] for label in labels}
    values = list(states.values())
    status = "Passed" if all(value == "Passed" for value in values) else "Failed" if "Failed" in values else "PairIncomplete" if "Passed" in values else "NotRun"
    return dict(status=status, plannedProcesses=len(labels), successfulProcesses=values.count("Passed"),
                failedProcesses=values.count("Failed"), notRunProcesses=values.count("NotRun"),
                positions=states)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = args.input.resolve()
    plan = json.loads((root / "screen-plan.json").read_text())
    exits = json.loads((root / "exits.json").read_text())
    result_path = root / "collection-result.json"
    collection = json.loads(result_path.read_text()) if result_path.exists() else None
    planned = expand_plan(plan)
    hashes = validate_identities(root, plan)
    observed, reports, boundary_by_label = {}, {}, {}
    environment = None
    seen_exit_labels = set()
    for row in exits:
        label = row["name"]
        if label in seen_exit_labels:
            raise ValueError("Duplicate process exit record: " + label)
        seen_exit_labels.add(label)
        if not label.startswith(("aa-", "ab-")):
            continue
        if label not in planned:
            raise ValueError("Unplanned measurement label/order/position: " + label)
        if type(row["exitCode"]) is not int:
            raise ValueError("Invalid process exit code: " + label)
        validate_command(row, planned[label], plan)
        path = root / "samples" / (label + ".txt")
        entry = dict(row, reportPresent=path.exists(), status="Failed" if row["exitCode"] else "Passed")
        observed[label] = entry
        if row["exitCode"] != 0:
            if path.exists():
                entry["reportSha256"] = digest(path)
            continue
        if not path.exists():
            raise ValueError("Successful process has no result: " + label)
        report = parse_report(path)
        settled = validate_report(report, planned[label], plan, hashes, label)
        current_environment = {key: report[key] for key in ENVIRONMENT_FIELDS}
        if plan["runtimePopulation"] != "nativeaot":
            current_environment["processExecutableSha256"] = report["processExecutableSha256"]
        if environment is None:
            environment = current_environment
        elif current_environment != environment:
            differences = [key for key in environment if current_environment.get(key) != environment[key]]
            raise ValueError("Process runtime/environment mismatch: " + label + ":" + ",".join(differences))
        entry.update(validate_samples(path, report))
        entry["measurementBoundary"] = "completed-and-settled" if settled else "completion-only"
        reports[label], boundary_by_label[label] = report, settled
    if len(set(boundary_by_label.values())) > 1:
        raise ValueError("One measurement source produced mixed completed/settled schemas")
    process_by_label = {}
    for label in planned:
        process_by_label[label] = observed.get(label, dict(name=label, exitCode=None,
            status="NotRun", reportPresent=(root / "samples" / (label + ".txt")).exists()))
        if label not in observed and process_by_label[label]["reportPresent"]:
            raise ValueError("Orphan report without an executed process: " + label)
    process_rows = list(process_by_label.values())
    overall = coverage(list(planned), process_by_label)
    if collection and collection.get("collectionSucceeded") is True and overall["status"] != "Passed":
        raise ValueError("Collection claims success but planned measurements failed or were not run")
    cells, aa_cells, aa_status = {}, {}, {}
    ab_groups, aa_groups = {}, {}
    for label, info in planned.items():
        groups = ab_groups if info["kind"] == "ab" else aa_groups
        groups.setdefault((info["round"], info["cell"]), {})[info["position"]] = (info["arm"], label)
    for (round_index, cell), members in sorted(ab_groups.items()):
        target = cells.setdefault(cell, dict(pairs=[], plannedPairs=[], metricChangesPercent={}, allocationDeltaBytesPerRpc=[]))
        for left in range(0, len(plan["abOrders"][round_index]), 2):
            pair = {members[position][0]: members[position][1] for position in (left, left + 1)}
            labels = [members[position][1] for position in (left, left + 1)]
            status = coverage(labels, process_by_label)
            target["plannedPairs"].append(dict(round=round_index, leftPosition=left, A=pair["A"], B=pair["B"], **status))
            if pair["A"] not in reports or pair["B"] not in reports:
                continue
            a, b = reports[pair["A"]], reports[pair["B"]]
            item = dict(round=round_index, leftPosition=left, A=pair["A"], B=pair["B"], metrics={})
            for metric in METRICS:
                if metric not in a or metric not in b:
                    continue
                av, bv = float(a[metric]), float(b[metric])
                item["metrics"][metric] = dict(A=av, B=bv, absoluteDelta=bv-av,
                                                 changePercent=(bv/av-1)*100 if av else None)
                if av:
                    target["metricChangesPercent"].setdefault(metric, []).append((bv/av-1)*100)
            target["allocationDeltaBytesPerRpc"].append(float(b["allocationBytesPerRpc"])-float(a["allocationBytesPerRpc"]))
            target["pairs"].append(item)
    for cell, result in cells.items():
        labels = [label for label, info in planned.items() if info["kind"] == "ab" and info["cell"] == cell]
        result.update(coverage(labels, process_by_label))
        result["metricChangesPercent"] = {key: summarize(values) for key, values in result["metricChangesPercent"].items()}
        result["allocationDeltaBytesPerRpc"] = summarize(result["allocationDeltaBytesPerRpc"])
        result["allFourPairsPresent"] = len(result["pairs"]) == len(result["plannedPairs"]) == 4
        result["allPlannedPairsPresent"] = len(result["pairs"]) == len(result["plannedPairs"])
        result["completedPairsAvailable"] = len(result["pairs"])
        result["settledPairsAvailable"] = sum("settledThroughputRpcPerSecond" in item["metrics"] for item in result["pairs"])
    for (round_index, cell), members in sorted(aa_groups.items()):
        target = aa_cells.setdefault(cell, {})
        left, right = members["left"][1], members["right"][1]
        aa_status.setdefault(cell, dict(plannedPairs=[]))["plannedPairs"].append(
            dict(round=round_index, left=left, right=right, **coverage([left, right], process_by_label)))
        if left not in reports or right not in reports:
            continue
        report, other = reports[left], reports[right]
        for metric in METRICS:
            if metric in report and metric in other and float(report[metric]):
                target.setdefault(metric, []).append((float(other[metric])/float(report[metric])-1)*100)
    aa_cells = {cell: {metric: summarize(values) for metric, values in metrics.items()}
                for cell, metrics in aa_cells.items()}
    for cell, result in aa_status.items():
        labels = [label for label, info in planned.items() if info["kind"] == "aa" and info["cell"] == cell]
        result.update(coverage(labels, process_by_label))
    settled_reports = sum(boundary_by_label.values())
    boundary = "no-successful-reports" if not reports else "completed-and-settled" if settled_reports else "completion-only"
    output = dict(classification="paired-process-generated-RPC-screen-not-full-acceptance",
                  plan=plan, collection=collection, productionGo=False,
                  note="Missing cells and failures remain missing/failing; no synthetic reruns or cross-source pooling.",
                  completedReports=len(reports), processRows=process_rows, processCoverage=overall,
                  cells=cells, aaCells=aa_cells, aaCellCoverage=aa_status,
                  verifiedPerRpcQuantileReports=len(reports), verifiedRuntimeBinaries=hashes,
                  verifiedEnvironment=environment, measurementBoundary=boundary,
                  boundaryAvailability=dict(completedReports=len(reports), settledReports=settled_reports,
                      completionOnlyReports=len(reports)-settled_reports,
                      settledCountersMayBeImputed=False,
                      settledObserverCosts=next(iter(reports.values())).get("settledObserverCosts") if reports else None),
                  planRecipe=dict(transports=TRANSPORTS, aaRounds=AA_ROUNDS,
                      warmupOpsPerWorker=WARMUP_OPS, minimumWarmupSecondsPerWorker=MINIMUM_WARMUP_SECONDS,
                      reason="Pinned architecture-screen v1 recipe; these constants were not separate plan fields."),
                  analysisProvenance=dict(scriptSha256=digest(Path(__file__)),
                      inputPlanSha256=digest(root / "screen-plan.json"),
                      inputExitsSha256=digest(root / "exits.json"),
                      inputCollectionSha256=digest(result_path) if result_path.exists() else None))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2, allow_nan=False) + "\n")
    print(f"{plan['runtimePopulation']}: {len(reports)}/{len(planned)} successful reports; "
          f"{overall['failedProcesses']} failed; {overall['notRunProcesses']} not run; boundary={boundary}")
    for cell, result in cells.items():
        metrics = result["metricChangesPercent"]
        values = [(key, round(metrics[key]["median"], 2)) for key in
                  ["throughputRpcPerSecond", "cpuMicrosecondsPerRpc", "p99Microseconds",
                   "settledThroughputRpcPerSecond", "settledCpuMicrosecondsPerRpc"] if key in metrics]
        print(cell, result["status"], f"{len(result['pairs'])}/{len(result['plannedPairs'])} pairs", values)


if __name__ == "__main__":
    main()
