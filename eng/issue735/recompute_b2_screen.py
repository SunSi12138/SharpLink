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
import re
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
INITIAL_DEV = "f8ba89121c87a6410a0139c44d7db30eab877f41"
GRANT_QUANTUM = 4096
GRANT_COUNTERS = [
    "b2ActivatedStreams", "b2DisposedStreams", "b2FirstProbeFallbacks",
    "b2LocalItems", "b2OwnerRefills", "b2ExactSlowAttempts",
    "b2RecalledGrantBytes", "b2RefundedItemBytes",
]
PAYLOAD_FIELDS = [
    "payloadKind", "payloadBytesMeaning", "generatedContract", "actualItemCodec",
    "actualItemCanExactSize", "actualEncodedItemBytes", "actualEncodedItemSha256",
    "actualUncompressedStreamDataFrameBytes", "codecPreflight",
    "codecPreflightIncludedInMeasurement",
]
PINNED_CASES = {
    "sized-dto": [
        ["Unary", 1, 16, 1, 32768], ["Server", 1, 16, 1, 16384],
        ["Server", 16, 16, 8, 512], ["Server", 1000, 16, 128, 32],
        ["Server", 1000, 64, 128, 32], ["Client", 1000, 16, 128, 32],
        ["Duplex", 1000, 16, 128, 32], ["Server", 100, 4096, 8, 128],
    ],
    "byte-array": [
        ["Unary", 1, 16, 1, 32768], ["Server", 1000, 16, 128, 32],
        ["Client", 1000, 16, 128, 32], ["Duplex", 1000, 16, 128, 32],
    ],
}
PINNED_AA_CASES = {
    "sized-dto": [["Server", 1000, 16, 128, 32], ["Server", 1000, 64, 128, 32]],
    "byte-array": [["Server", 1000, 16, 128, 32]],
}


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


def integer(values, key):
    value = values[key]
    if re.fullmatch(r"0|[1-9][0-9]*", value) is None:
        raise ValueError("Non-integer or negative count: " + key + ":" + value)
    return int(value)


def validate_payload(report, work, plan, label):
    """Check codec bytes, not an inferred RPC frame count or measured wire total."""
    kind, size = plan["payloadKind"], work["payloadBytes"]
    transits = 2 if work["shape"] == "Unary" else work["itemsPerRpc"] * (
        2 if work["shape"] == "Duplex" else 1)
    expected = {"payloadKind": kind, "codecPreflightIncludedInMeasurement": "false",
                "wireDataFrameCount": "not-instrumented-not-inferred-from-logical-items",
                "wireBytes": "not-instrumented"}
    if kind == "sized-dto":
        if size < 10 or size % 2:
            raise ValueError("Invalid requested exact-size DTO payload: " + label)
        characters = (size - 10) // 2
        text = "".join(chr(ord("a") + (index * 17 + 11) % 26) for index in range(characters))
        # Pinned generated encoding on the recorded X64 subject: present=1;
        # field 1 LengthDelimited=14, UTF-16 byte length; field 2 Fixed2=18;
        # ushort tag; object terminator=0. This oracle checks the emitted-byte hash.
        wire = (b"\x01\x0e" + (characters * 2).to_bytes(4, "little") +
                text.encode("utf-16-le") + b"\x12" + (0x735B).to_bytes(2, "little") + b"\x00")
        expected.update({
            "generatedContract": "SharpLink.Benchmarks.IGrantBenchmarkRpc",
            "payloadBytesMeaning": "target-encoded-dto-item-bytes-including-codec-framing",
            "actualItemCanExactSize": "true", "actualDtoTextCharacters": characters,
            "actualDtoTag": 0x735B,
            "expectedEncodedItemBytesPerRpc": transits * size,
            "expectedTextCodeUnitsPerRpc": transits * characters,
            "codecPreflight": "generated-sized-vs-regular-byte-identity-and-full-roundtrip",
        })
        if "expectedApplicationPayloadBytesPerRpc" in report:
            raise ValueError("Encoded DTO bytes mislabeled as application byte-array bytes: " + label)
        if re.fullmatch(r"SharpLink\.Generated\.__SharpLinkGeneratedCodec_[0-9A-F]{16}",
                        report["actualItemCodec"]) is None:
            raise ValueError("The sized DTO did not use a generated native codec: " + label)
    else:
        payload = bytearray(size)
        payload[0], payload[-1] = (23, 47) if size == 4096 else (17, 31)
        wire = size.to_bytes(4, "little") + payload
        expected.update({
            "generatedContract": "SharpLink.Benchmarks.IBenchmarkRpc",
            "payloadBytesMeaning": "application-byte-array-length-excluding-codec-framing",
            "actualItemCanExactSize": "false",
            "expectedApplicationPayloadBytesPerRpc": transits * size,
            "codecPreflight": "original-unsized-codec-full-roundtrip",
        })
        if any(key in report for key in ["actualDtoTextCharacters", "actualDtoTag",
                                        "expectedEncodedItemBytesPerRpc", "expectedTextCodeUnitsPerRpc"]):
            raise ValueError("Byte-array fallback includes DTO-only payload units: " + label)
        if not report["actualItemCodec"].startswith("SharpLink.Runtime.BlitArrayCodec`1[[System.Byte,"):
            raise ValueError("The fallback control did not use the original byte-array codec: " + label)
    expected.update({"actualEncodedItemBytes": len(wire),
                     "actualUncompressedStreamDataFrameBytes": len(wire) + 17,
                     "actualEncodedItemSha256": hashlib.sha256(wire).hexdigest().upper()})
    expect(report, expected, label + ":payload-and-codec")


def validate_grants(report, planned, plan, label):
    enabled = planned["arm"] == "B"
    work = planned["workload"]
    expect(report, {
        "b2ProducerGrantEnabled": str(enabled).lower(),
        "b2GrantQuantumBytes": GRANT_QUANTUM if enabled else 0,
        "b2Scope": "server-generated-outbound-first-sized-admission-only",
        "b2LiveHandlesAfterSettlement": 0,
        "b2CounterScope": "per-context-local-writes-and-once-per-stream-aggregation-included-in-candidate-cost",
        "b2ExactSlowUnit": "admission-attempt-not-successful-wire-item",
        "b2QueueOrWriterChange": "false",
        "b2UnusedCreditPeak": "not-instrumented-global-per-item-counter-deliberately-absent",
        "b2UnusedCreditBound": "min(connection-window,active-grant-streams-times-4096)",
        "schedulerCoordinationCounters": "B2-context-counters-included-in-candidate-cost;legacy-owner-and-SendPump-not-instrumented",
    }, label + ":grant-instrumentation")
    counters = {key: integer(report, key) for key in GRANT_COUNTERS}
    active = counters["b2ActivatedStreams"]
    if active != counters["b2DisposedStreams"]:
        raise ValueError("Measured B2 grant contexts did not settle: " + label)
    eligible = (enabled and plan["payloadKind"] == "sized-dto" and
                work["shape"] in {"Server", "Duplex"} and work["itemsPerRpc"] > 0)
    if not eligible:
        if any(counters.values()):
            raise ValueError("B2 grant activity outside the declared eligible workload: " + label)
        return counters
    rpc_count = integer(report, "successfulRpcs")
    if active <= 0 or active + counters["b2FirstProbeFallbacks"] != rpc_count:
        raise ValueError("B2 first-item attempts do not cover each eligible RPC or silently fell back: " + label)
    subsequent_items = active * (work["itemsPerRpc"] - 1)
    local, refills, slow = (counters[key] for key in
                           ["b2LocalItems", "b2OwnerRefills", "b2ExactSlowAttempts"])
    if local + refills > subsequent_items or local + refills + slow < subsequent_items:
        raise ValueError("B2 item/admission counters cannot cover the actual activated stream items: " + label)
    if work["itemsPerRpc"] == 1 and any([local, refills, slow, counters["b2RecalledGrantBytes"]]):
        raise ValueError("A one-item B2 stream reported subsequent-item grants: " + label)
    if work["itemsPerRpc"] > 2 and work["payloadBytes"] <= 64 and local <= 0:
        raise ValueError("No local grant consumption in the declared small-item B2 workload: " + label)
    if work["payloadBytes"] >= GRANT_QUANTUM and local != 0:
        raise ValueError("An item at least as large as the total grant quantum used unused local credit: " + label)
    size = work["payloadBytes"]
    recalled = counters["b2RecalledGrantBytes"]
    if recalled > refills * max(0, GRANT_QUANTUM - size) or recalled % size:
        raise ValueError("Unused-grant byte accounting exceeds admitted grants or uses the wrong unit: " + label)
    if counters["b2RefundedItemBytes"] != 0:
        raise ValueError("Fully validated successful fixed-size RPCs unexpectedly refunded unsent items: " + label)
    return counters


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
    kind = plan.get("payloadKind")
    if kind not in PINNED_CASES or plan["cases"] != PINNED_CASES[kind] or plan["aaCases"] != PINNED_AA_CASES[kind]:
        raise ValueError("The plan differs from the predeclared B2 payload-specific screening matrix")
    if plan["abOrders"] != [["A", "B", "B", "A"], ["B", "A", "A", "B"]]:
        raise ValueError("The pinned B2 screen requires four interleaved adjacent A/B pairs")
    if (plan.get("initialDevBaselineSha") != INITIAL_DEV or
            plan.get("correctnessControlSha") != plan.get("baselineSha") or
            re.fullmatch(r"[0-9a-f]{40}", plan.get("baselineSha", "")) is None or
            re.fullmatch(r"[0-9a-f]{40}", plan.get("candidateSha", "")) is None or
            plan.get("measurementRecipeSha") != plan.get("candidateSha") or
            plan.get("grantQuantumBytes") != GRANT_QUANTUM):
        raise ValueError("The initial-dev/common-correctness-control/grant-quantum identity changed")
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
    for key in ["MeasurementSourceSha", "SendPumpSourceSha256",
                "approvedProductionManifestSha256", "measurementSourceManifest", "sendPumpSourceManifest"]:
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
        expected_mode = "correctness-fixed-baseline" if arm == "A" else "B2-server-grant-4096"
        if (identity.get("schema") != "sharplink-b2-server-grant-build-identity-v1" or
                identity.get("InitialDevBaselineSha") != INITIAL_DEV or
                identity.get("CorrectnessControlSha") != plan["correctnessControlSha"] or
                identity.get("Mode") != expected_mode or
                identity.get("b2ProducerGrantEnabled") is not (arm == "B")):
            raise ValueError("Compiled source mode/common correctness control mismatch: " + arm)
        if identity["BaselineSha"] != plan["baselineSha"] or identity["CandidateSha"] != (
                plan["baselineSha"] if arm == "A" else plan["candidateSha"]):
            raise ValueError("Plan/source commit mismatch: " + arm)
        for key in ["BaselineSha", "CandidateSha", "RuntimeSourceTreeSha", "MeasurementSourceSha",
                    "ControllerSourceSha256", "SendPumpSourceSha256", "binarySha256",
                    "approvedProductionManifestSha256", "compiledIdentityFileSha256"]:
            value = identity[key]
            size = 40 if key in {"BaselineSha", "CandidateSha", "RuntimeSourceTreeSha"} else 64
            if re.fullmatch(r"[0-9a-fA-F]{" + str(size) + "}", value) is None:
                raise ValueError("Invalid source identity SHA: " + arm + ":" + key)
        if not identity["Mode"] or identity["productionScheduler"] != "dev-SendPump" or identity["b3ReadyWriterEnabled"] is not False:
            raise ValueError("Unrecognized production scheduler identity: " + arm)
        identity_path = root / ("identity-" + arm + ".json")
        if json.loads(identity_path.read_text()) != {key: value for key, value in identity.items() if key != "binarySha256"}:
            raise ValueError("The plan changed the original successful source-verification record: " + arm)
        if identity.get("runtimeSourceTreeVerification") != "all-production-source-paths-and-working-blobs-match-recorded-tree":
            raise ValueError("Unverified working production source tree: " + arm)
        changes = identity["onlyAllowedProductionDifference"]
        required_changes = {"src/SharpLink.Runtime/StreamFlowController.cs",
                            "src/SharpLink.Runtime/StreamFlowController.SendGrants.cs",
                            "src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs",
                            "src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs"}
        if arm == "A" and changes:
            raise ValueError("Baseline source contains a performance overlay")
        if arm == "B" and (not required_changes.issubset(changes) or any(
                path not in required_changes and not (
                    path.startswith("src/SharpLink.Runtime/StreamFlowController.SendGrants.") and path.endswith(".cs"))
                for path in changes)):
            raise ValueError("Candidate production scope exceeds the bounded B2 producer pilot")
        binary = root / "binaries" / arm / binary_name
        if digest(binary) != identity["binarySha256"]:
            raise ValueError("Binary hash mismatch: " + arm)
        if aot:
            with binary.open("rb") as source:
                if source.read(4) != b"\x7fELF":
                    raise ValueError("NativeAOT subject is not the published Linux ELF: " + arm)
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
        "schema": "sharplink-b2-server-grant-evidence-v1", "status": "passed",
        "initialDevBaselineSha": INITIAL_DEV, "correctnessControlSha": plan["correctnessControlSha"],
        "candidateSha": identity["CandidateSha"], "baselineSha": plan["baselineSha"],
        "runtimeSourceTreeSha": identity["RuntimeSourceTreeSha"],
        "measurementSourceSha": identity["MeasurementSourceSha"],
        "controllerSourceSha256": identity["ControllerSourceSha256"],
        "sendPumpSourceSha256": identity["SendPumpSourceSha256"], "compiledMode": identity["Mode"],
        "productionScheduler": "dev-SendPump", "b3ReadyWriterEnabled": "false",
        "samePhysicalSessionBeforeAndAfter": "true", "builtForNativeAot": str(aot).lower(),
        "dynamicCodeSupported": str(not aot).lower(), "dynamicCodeCompiled": str(not aot).lower(),
        "processorCount": len(plan["cpuAffinity"]), "warmupOpsPerWorker": WARMUP_OPS,
        "architecture": "X64", "tierStabilizationVerified": "false",
        "minimumWarmupSecondsPerWorker": MINIMUM_WARMUP_SECONDS,
        "warmupPolicy": "each-worker-runs-until-fixed-minimum-ops-and-minimum-duration-both-met",
        "successfulRpcs": work["concurrency"] * work["measuredOpsPerWorker"], "failedRpcs": 0,
        "threadPoolConfiguredWorkerMinimum": max(32, len(plan["cpuAffinity"]) * 4),
        "env.DOTNET_PROCESSOR_COUNT": len(plan["cpuAffinity"]), "env.DOTNET_ReadyToRun": "0",
        "env.DOTNET_TieredCompilation": "<unset>" if aot else "1",
        "env.DOTNET_TieredPGO": "<unset>" if aot else "0" if plan["runtimePopulation"] == "jit-pgo0" else "1",
        "env.GITHUB_SHA": plan["measurementRecipeSha"],
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
    if not all(present):
        raise ValueError("B2 screening requires both completed and settled measurements: " + label)
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
    expect(report, {"logicalItems": logical, "expectedStreamPayloadItemsPerRpc": stream_items}, label)
    validate_payload(report, work, plan, label)
    validate_grants(report, planned, plan, label)
    close(number(report, "throughputLogicalItemsPerSecond"), logical / seconds, label + ":items")
    close(number(report, "throughputStreamPayloadItemsPerSecond"), count * stream_items / seconds, label + ":stream-items")
    for field, total in [("cpuMicrosecondsPerLogicalItem", "cpuTotalMicroseconds"),
                         ("allocationBytesPerLogicalItem", "allocationBytesTotal")]:
        if logical:
            close(number(report, field), number(report, total) / logical, label + ":" + field)
        else:
            expect(report, {field: "N/A"}, label + ":empty-stream-unit")
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
        close(number(report, "settledThroughputLogicalItemsPerSecond"), logical / settled_seconds, label + ":settled-item-throughput")
        close(number(report, "settledCpuMicrosecondsPerRpc"), number(report, "settledCpuTotalMicroseconds") / count, label + ":settled-CPU")
        close(number(report, "settledAllocationBytesPerRpc"), number(report, "settledAllocationBytesTotal") / count, label + ":settled-allocation")
        close(number(report, "completedToSettledMicroseconds"), (settled_seconds - seconds) * 1e6, label + ":settled-elapsed")
        for key in ["settledGen0Collections", "settledGen1Collections", "settledGen2Collections", "settledGcPauseTotalMicroseconds"]:
            number(report, key)
        for field, total in [("settledCpuMicrosecondsPerLogicalItem", "settledCpuTotalMicroseconds"),
                             ("settledAllocationBytesPerLogicalItem", "settledAllocationBytesTotal")]:
            if logical:
                close(number(report, field), number(report, total) / logical, label + ":" + field)
            else:
                expect(report, {field: "N/A"}, label + ":empty-stream-unit")
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
    payload_by_size = {}
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
        payload_fields = PAYLOAD_FIELDS + (["actualDtoTextCharacters", "actualDtoTag"]
                                          if plan["payloadKind"] == "sized-dto" else [])
        payload_metadata = {key: report[key] for key in payload_fields}
        size = planned[label]["workload"]["payloadBytes"]
        if size in payload_by_size and payload_by_size[size] != payload_metadata:
            raise ValueError("A/B/AA codec identity or actual item bytes changed for one payload size: " + label)
        payload_by_size[size] = payload_metadata
        entry["verifiedPayloadEvidence"] = payload_metadata
        entry["b2Counters"] = {key: integer(report, key) for key in GRANT_COUNTERS}
        entry["b2CountersPerSuccessfulRpc"] = {key: value / int(report["successfulRpcs"])
                                                for key, value in entry["b2Counters"].items()}
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
        target = cells.setdefault(cell, dict(pairs=[], plannedPairs=[], metricChangesPercent={},
                                            allocationDeltaBytesPerRpc=[], candidateGrantEvidence=[]))
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
            grant = {key: integer(b, key) for key in GRANT_COUNTERS}
            active, items, count = grant["b2ActivatedStreams"], int(b["itemsPerRpc"]), int(b["successfulRpcs"])
            target["candidateGrantEvidence"].append(dict(
                process=pair["B"], counters=grant,
                activatedContextsPerSuccessfulRpc=active / count,
                localAdmissionsPerSuccessfulRpc=grant["b2LocalItems"] / count,
                localFractionOfActivatedStreamItems=(grant["b2LocalItems"] / (active * items)
                                                    if active and items else None),
                ownerRefillsPerSuccessfulRpc=grant["b2OwnerRefills"] / count,
                exactSlowAttemptsPerSuccessfulRpc=grant["b2ExactSlowAttempts"] / count))
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
                  payloadKind=plan["payloadKind"], verifiedPayloadByRequestedSize=payload_by_size,
                  plan=plan, collection=collection, productionGo=False,
                  note="Missing cells and failures remain missing/failing; no synthetic reruns or cross-source pooling.",
                  completedReports=len(reports), processRows=process_rows, processCoverage=overall,
                  cells=cells, aaCells=aa_cells, aaCellCoverage=aa_status,
                  verifiedPerRpcQuantileReports=len(reports), verifiedRuntimeBinaries=hashes,
                  verifiedEnvironment=environment, measurementBoundary=boundary,
                  grantEvidence=dict(counterNames=GRANT_COUNTERS,
                      validatedSuccessfulReports=len(reports),
                      allSuccessfulReportsPassedActivationAndSettlementChecks=bool(reports),
                      counterScope="measured interval through stream settlement; context-local writes aggregate once per disposed context",
                      exactSlowUnit="owner admission attempt, not a successful wire item or a physical lock count",
                      firstProbeRule="one per nonempty sized generated server-output RPC; activation plus fallback equals successful RPCs",
                      nonEligibleRule="all eight counters zero on baseline, byte-array, Unary and Client controls",
                      queueWriterCountersMeasured=False, unusedGrantPeakMeasured=False,
                      measuredWireFrameCountOrTotalBytes=False),
                  boundaryAvailability=dict(completedReports=len(reports), settledReports=settled_reports,
                      completionOnlyReports=len(reports)-settled_reports,
                      settledCountersMayBeImputed=False,
                      settledObserverCosts=next(iter(reports.values())).get("settledObserverCosts") if reports else None),
                  planRecipe=dict(transports=TRANSPORTS, aaRounds=AA_ROUNDS,
                      warmupOpsPerWorker=WARMUP_OPS, minimumWarmupSecondsPerWorker=MINIMUM_WARMUP_SECONDS,
                      reason="Pinned B2 payload-specific matrix; no cross-payload or cross-measurement-source pooling."),
                  analysisProvenance=dict(scriptSha256=digest(Path(__file__)),
                      inputPlanSha256=digest(root / "screen-plan.json"),
                      inputExitsSha256=digest(root / "exits.json"),
                      inputCollectionSha256=digest(result_path) if result_path.exists() else None))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2, allow_nan=False) + "\n")
    print(f"{plan['runtimePopulation']}/{plan['payloadKind']}: {len(reports)}/{len(planned)} successful reports; "
          f"{overall['failedProcesses']} failed; {overall['notRunProcesses']} not run; boundary={boundary}")
    for cell, result in cells.items():
        metrics = result["metricChangesPercent"]
        values = [(key, round(metrics[key]["median"], 2)) for key in
                  ["throughputRpcPerSecond", "cpuMicrosecondsPerRpc", "p99Microseconds",
                   "settledThroughputRpcPerSecond", "settledCpuMicrosecondsPerRpc"] if key in metrics]
        print(cell, result["status"], f"{len(result['pairs'])}/{len(result['plannedPairs'])} pairs", values)


if __name__ == "__main__":
    main()
