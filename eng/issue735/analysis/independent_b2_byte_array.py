#!/usr/bin/env python3
"""Independent audit of the three 5cab byte-array RPC artifact populations.

Only Python stdlib is imported. Metrics are calculated from original report
totals and original per-RPC CSV timestamps. paired-summary.json and either
primary recomputation script are never parsed or imported. ZIP entries are
byte-checked, including otherwise uninterpreted original summaries.
"""

import argparse
import bisect
import csv
from decimal import Decimal
import hashlib
import io
import json
import math
from pathlib import Path, PurePosixPath
import statistics
import struct
import subprocess
import zipfile


BASE = "c7ba1768f31ce7219dc438dc6009347de6806ed5"
CANDIDATE = "5cabd4d7cc5ce9aa0a87424e36954e414e0912da"
INITIAL = "f8ba89121c87a6410a0139c44d7db30eab877f41"
CASES = [("Unary", 1, 16, 1, 32768), ("Server", 1000, 16, 128, 32),
         ("Client", 1000, 16, 128, 32), ("Duplex", 1000, 16, 128, 32)]
COUNTERS = ["b2ActivatedStreams", "b2DisposedStreams", "b2FirstProbeFallbacks",
            "b2LocalItems", "b2OwnerRefills", "b2ExactSlowAttempts",
            "b2RecalledGrantBytes", "b2RefundedItemBytes"]
CHANGES = {
    "src/SharpLink.Runtime/StreamFlowController.cs": "8f40d07367532bea3890c3a8015c29fc911ff7a49f349b1bfe5271ca30f1cd24",
    "src/SharpLink.Runtime/StreamFlowController.SendGrants.cs": "df37087330c876d1ad91bc87966c983d2d633d5ca5fbead662c3097362f2071d",
    "src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs": "2604d69b91c0d67f62b71ad54ecb500c75dbdf66ed5c9e3dc2d7d9f4353ed396",
    "src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs": "57d829c047ccf2098fd3789c69fb5d87d4fe9e73adcaff23a3e12a4ab190ecba",
}
IDENTITY_FIELDS = {
    "InitialDevBaselineSha": "initialDevBaselineSha",
    "CorrectnessControlSha": "correctnessControlSha", "Mode": "compiledMode",
    "BaselineSha": "baselineSha", "CandidateSha": "candidateSha",
    "RuntimeSourceTreeSha": "runtimeSourceTreeSha", "MeasurementSourceSha": "measurementSourceSha",
    "ControllerSourceSha256": "controllerSourceSha256", "SendPumpSourceSha256": "sendPumpSourceSha256",
}
SAMPLE_HEADER = ["worker", "operation", "start_timestamp", "end_timestamp", "latency_ticks",
                 "start_us_from_gate", "end_us_from_gate", "latency_us"]


def require(condition, context):
    if not condition:
        raise ValueError(context)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def save(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n")


def close(actual, expected, label, absolute=1e-6):
    require(math.isfinite(actual) and math.isfinite(expected)
            and math.isclose(actual, expected, rel_tol=2e-11, abs_tol=absolute),
            f"{label}: actual {actual!r}, expected {expected!r}")


def kv(path):
    result = {}
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if not line:
            continue
        require("=" in line, f"malformed key/value in {path.name}")
        key, value = line.split("=", 1)
        require(key not in result, f"duplicate key {key} in {path.name}")
        result[key] = value
    return result


def git(repo, *args):
    return subprocess.check_output(["git", "-C", str(repo), *args])


def tree(repo, ref, prefix):
    result = {}
    for row in git(repo, "ls-tree", "-rz", ref, "--", prefix).split(b"\0"):
        if row:
            meta, path = row.split(b"\t", 1)
            mode, kind, digest = meta.decode().split()
            require(kind == "blob" and mode in ("100644", "100755"), "unexpected tree entry")
            result[path.decode()] = digest
    return result


def blobs(repo, ids):
    ordered = sorted(set(ids))
    if not ordered:
        return {}
    out = subprocess.check_output(
        ["git", "-C", str(repo), "cat-file", "--batch"], input=("\n".join(ordered) + "\n").encode())
    stream = io.BytesIO(out)
    result = {}
    for expected in ordered:
        obj, kind, size = stream.readline().decode().split()
        require(obj == expected and kind == "blob", "git batch response mismatch")
        data = stream.read(int(size))
        require(stream.read(1) == b"\n", "git batch missing delimiter")
        require(hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest() == obj,
                "git blob digest mismatch")
        result[obj] = data
    require(stream.read() == b"", "extra git batch response")
    return result


def manifest_digest(records, data_by_path):
    h = hashlib.sha256()
    require([r["path"] for r in records] == sorted(data_by_path), "manifest path order/set")
    for row in records:
        name = row["path"]
        data = data_by_path[name]
        require(row["sha256"] == sha(data) and row["bytes"] == len(data), "manifest " + name)
        namebytes = name.encode()
        h.update(len(namebytes).to_bytes(8, "big"))
        h.update(namebytes)
        h.update(len(data).to_bytes(8, "big"))
        h.update(data)
    return h.hexdigest()


def validate_sources(repo, raw, plan):
    ids = {a: load(raw / f"identity-{a}.json") for a in "AB"}
    entries = {a: tree(repo, rev, "src") for a, rev in [("A", BASE), ("B", CANDIDATE)]}
    initial_entries = tree(repo, INITIAL, "src")
    require(len(entries["A"]) == 496 and len(entries["B"]) == 497, "production path counts")
    differences = {p for p in set(entries["A"]) | set(entries["B"])
                   if entries["A"].get(p) != entries["B"].get(p)}
    require(differences == set(CHANGES), "candidate contains an unapproved production difference")
    control_changes = {p for p in set(entries["A"]) | set(initial_entries)
                       if entries["A"].get(p) != initial_entries.get(p)}
    require(control_changes == {"src/SharpLink.Runtime/StreamFlowController.cs",
                               "src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs"},
            "common control has an extra production change")
    all_data = blobs(repo, list(entries["A"].values()) + list(entries["B"].values()))
    for path, digest in CHANGES.items():
        require(sha(all_data[entries["B"][path]]) == digest, "reviewed candidate source " + path)
    require(sha(all_data[entries["A"]["src/SharpLink.Runtime/StreamFlowController.cs"]])
            == "0c1b1f2565697fb6dfa6097e16fdceda28d3ba4aba032ba28b3d8933ab223af4", "c7 controller")
    require(sha(all_data[entries["A"]["src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs"]])
            == "531753a3f2fb54f1a596b854d3af30cd4f3625eef3c2bcfc955794eb1dd64075", "c7 bridge")
    manifests = {}
    for arm, rev in [("A", BASE), ("B", CANDIDATE)]:
        identity = ids[arm]
        plan_identity = plan["sourceTrees"][arm]
        require(identity == {k: v for k, v in plan_identity.items() if k != "binarySha256"}
                and "binarySha256" in plan_identity, "plan/identity discrepancy")
        require(identity["CandidateSha"] == rev and identity["BaselineSha"] == BASE
                and identity["CorrectnessControlSha"] == BASE and identity["InitialDevBaselineSha"] == INITIAL,
                "fixed source identities")
        require(identity["RuntimeSourceTreeSha"] == git(repo, "rev-parse", rev + "^{tree}").decode().strip(),
                "runtime tree differs from fixed commit")
        require(identity["productionSourceFilesChecked"] == len(entries[arm]) and
                tree(repo, identity["RuntimeSourceTreeSha"], "src") == entries[arm], "runtime source tree")
        require(identity["ControllerSourceSha256"] == sha(all_data[entries[arm]["src/SharpLink.Runtime/StreamFlowController.cs"]]),
                "compiled controller digest does not match actual Git source")
        require(identity["onlyAllowedProductionDifference"] == ([] if arm == "A" else sorted(CHANGES)),
                "identity production-difference allowance")
        manifests[arm] = [{"path": p, "gitBlob": oid, "bytes": len(all_data[oid]),
                           "sha256": sha(all_data[oid])} for p, oid in sorted(entries[arm].items())]
        m = identity["measurementSourceManifest"]
        require(len(m) == 18 and len({r["path"] for r in m}) == 18, "measurement input count")
        data = {r["path"]: git(repo, "show", CANDIDATE + ":" + r["path"]) for r in m}
        require(manifest_digest(m, data) == identity["MeasurementSourceSha"], "measurement digest")
        approved_bytes = data["test/SharpLink.FlowArchitectureEvidence/B2ProductionManifest.json"]
        require(sha(approved_bytes) == identity["approvedProductionManifestSha256"]
                and json.loads(approved_bytes)["changes"] == CHANGES, "reviewed production manifest identity")
        for name, content in data.items():
            if not name.startswith("test/SharpLink.FlowArchitectureEvidence/"):
                require(content == git(repo, "show", BASE + ":" + name), "shared baseline fixture/build policy")
        pump_records = identity["sendPumpSourceManifest"]
        pump_data = {r["path"]: all_data[entries[arm][r["path"]]] for r in pump_records}
        require(manifest_digest(pump_records, pump_data) == identity["SendPumpSourceSha256"], "SendPump digest")
        expected_mode = "correctness-fixed-baseline" if arm == "A" else "B2-server-grant-4096"
        require(identity["Mode"] == expected_mode and identity["productionScheduler"] == "dev-SendPump"
                and identity["b3ReadyWriterEnabled"] is False and identity["b2ProducerGrantEnabled"] == (arm == "B"),
                "compiled scheduler identity")
        fields = list(IDENTITY_FIELDS)
        lines = ["namespace SharpLink.Benchmarks;", "",
                 "// Generated by stamp_identity.py after exact source/tree validation.",
                 "// This file is compilation metadata; runtime arguments only verify these values.",
                 "internal static class ModeIdentity", "{"]
        lines.extend(f'    internal const string {key} = "{identity[key]}";' for key in fields)
        lines.extend(["}", ""])
        require(sha("\n".join(lines).encode()) == identity["compiledIdentityFileSha256"], "generated identity source hash")
    require(ids["A"]["MeasurementSourceSha"] == ids["B"]["MeasurementSourceSha"], "A/B measurement mismatch")
    require(ids["A"]["sendPumpSourceManifest"] == ids["B"]["sendPumpSourceManifest"], "SendPump changed")
    return ids, manifests


def schedule():
    result = []
    for repetition, order in enumerate(("ABBA", "BAAB")):
        transports = ["tcp", "sharedmemory"] if repetition == 0 else ["sharedmemory", "tcp"]
        cases = CASES if repetition == 0 else list(reversed(CASES))
        for transport in transports:
            for shape, items, size, concurrency, operations in cases:
                for position, arm in enumerate(order):
                    label = f"ab-r{repetition}-{transport}-{shape}-{items}x{size}-c{concurrency}-p{position}-{arm}"
                    result.append(dict(name=label, kind="AB", round=repetition, position=position, arm=arm,
                                       transport=transport, shape=shape, items=items, bytes=size,
                                       concurrency=concurrency, operations=operations))
    for transport in ["tcp", "sharedmemory"]:
        for repetition in [0, 1]:
            for side in (["left", "right"] if repetition == 0 else ["right", "left"]):
                label = f"aa-r{repetition}-{transport}-Server-1000x16-c128-{side}"
                result.append(dict(name=label, kind="AA", round=repetition, side=side, arm="A",
                                   transport=transport, shape="Server", items=1000, bytes=16,
                                   concurrency=128, operations=32))
    return result


def validate_sample(raw, row, step, identities, population, binary_data):
    label, arm = row["name"], row["arm"]
    report = raw / "samples" / (label + ".txt")
    values = kv(report)
    require(values.get("status") == "passed", "sample did not report passed")
    for key, report_key in IDENTITY_FIELDS.items():
        require(values[report_key] == identities[arm][key], "report identity " + key)
    expected = {"shape": row["shape"], "transport": row["transport"], "payloadKind": "byte-array",
                "itemsPerRpc": str(row["items"]), "payloadBytes": "16", "concurrency": str(row["concurrency"]),
                "measuredOpsPerWorker": str(row["operations"]), "warmupOpsPerWorker": "2",
                "minimumWarmupSecondsPerWorker": "5", "tierStabilizationVerified": "false",
                "b2ProducerGrantEnabled": str(arm == "B").lower(), "b3ReadyWriterEnabled": "false",
                "b2GrantQuantumBytes": "4096" if arm == "B" else "0", "productionScheduler": "dev-SendPump",
                "runtime": ".NET 10.0.12", "runtimeVersion": "10.0.12", "processorCount": "4",
                "architecture": "X64", "os": "Ubuntu 24.04.5 LTS", "serverGc": "false",
                "protocol": "v2-default-unmodified", "compression": "default-no-providers",
                "flushPolicy": "profile-default-no-override", "performanceProfile": "Balanced",
                "requestTimeout": "disabled-as-in-existing-BenchmarkEnvironment",
                "heartbeat": "transport-default" if row["transport"] == "tcp" else "5min-period-10min-timeout-as-in-existing-BenchmarkEnvironment",
                "env.GITHUB_RUN_ID": "38173700315", "env.GITHUB_RUN_ATTEMPT": "1", "env.GITHUB_SHA": CANDIDATE,
                "env.DOTNET_PROCESSOR_COUNT": "4", "env.DOTNET_ReadyToRun": "0",
                "actualClientStreamReceiveWindowBytes": "1048576", "actualClientConnectionReceiveWindowBytes": "16777216",
                "actualClientMaxSendQueueBytes": "8388608", "actualClientMaxPreCreditSerializedBytes": "4194304",
                "actualItemCanExactSize": "false", "actualEncodedItemBytes": "20", "actualUncompressedStreamDataFrameBytes": "37",
                "codecPreflight": "original-unsized-codec-full-roundtrip", "codecPreflightIncludedInMeasurement": "false",
                "generatedContract": "SharpLink.Benchmarks.IBenchmarkRpc", "samePhysicalSessionBeforeAndAfter": "true",
                "physicalReadyConnectionsAfter": "1", "failedRpcs": "0", "b2LiveHandlesAfterSettlement": "0",
                "threadPoolConfiguredWorkerMinimum": "32", "quantileMethod": "nearest-rank",
                "wireDataFrameCount": "not-instrumented-not-inferred-from-logical-items", "wireBytes": "not-instrumented"}
    for field in ["COMPlus_TieredPGO", "COMPlus_TieredCompilation", "COMPlus_ReadyToRun", "COMPlus_TC_QuickJitForLoops",
                  "DOTNET_TC_QuickJitForLoops", "DOTNET_gcServer", "DOTNET_GCHeapCount", "DOTNET_GCConserveMemory",
                  "DOTNET_ThreadPool_UnfairSemaphoreSpinLimit"]:
        expected["env." + field] = "<unset>"
    native = population == "nativeaot"
    expected.update({"builtForNativeAot": str(native).lower(), "dynamicCodeSupported": str(not native).lower(),
                     "dynamicCodeCompiled": str(not native).lower(),
                     "env.DOTNET_TieredCompilation": "<unset>" if native else "1",
                     "env.DOTNET_TieredPGO": "<unset>" if native else population[-1]})
    for key, value in expected.items():
        require(values.get(key) == value, f"{label} {key}: {values.get(key)!r} != {value!r}")
    for key in COUNTERS + ["pendingCallsAfterSettlement", "activeClientCallsAfterSettlement",
                           "activeClientStreamsAfterSettlement", "activeServerCallsAfterSettlement"]:
        require(key in values and int(values[key]) == 0, label + " nonzero/missing " + key)
    require(values["actualItemCodec"].startswith("SharpLink.Runtime.BlitArrayCodec`1[[System.Byte,"), "codec type")
    payload = struct.pack("<i", 16) + bytes([17]) + bytes(14) + bytes([31])
    require(values["actualEncodedItemSha256"].lower() == sha(payload), "actual encoded fixture hash")
    executable_name = "SharpLink.Benchmarks" if native else "SharpLink.Benchmarks.dll"
    bin_relative = f"binaries/{arm}/{executable_name}"
    require(values["measuredBinary"].endswith("/" + bin_relative), "binary arm/path")
    require(sha(binary_data[bin_relative]) == values["measuredBinarySha256"], "actual application binary hash")
    if native:
        require(binary_data[bin_relative][:4] == b"\x7fELF", "published native executable is not ELF")
        require(values["processExecutable"] == values["measuredBinary"]
                and values["processExecutableSha256"] == values["measuredBinarySha256"], "native direct executable")
    else:
        runtime_path = f"binaries/{arm}/SharpLink.Runtime.dll"
        require(values["runtimeAssembly"].endswith("/" + runtime_path)
                and values["runtimeAssemblySha256"] == sha(binary_data[runtime_path]), "actual runtime DLL")
        for key in IDENTITY_FIELDS:
            require(identities[arm][key].encode("utf-16le") in binary_data[bin_relative], "JIT embedded identity " + key)
    command = step["command"]
    require(command[:3] == ["taskset", "-c", "0,1,2,3"], "CPU affinity command")
    offset = 3 if native else 4
    if not native:
        require(command[3] == "dotnet", "JIT invocation")
    require(command[offset] == values["measuredBinary"], "executed binary path")
    require(command[offset + 1:] == [row["transport"], row["shape"], str(row["items"]), "16",
            str(row["concurrency"]), "2", str(row["operations"]), BASE, identities[arm]["RuntimeSourceTreeSha"],
            identities[arm]["Mode"], values["rawSamplesFile"].removesuffix(".samples.csv")], "actual load command")
    require(values["rawSamplesFile"].endswith("/samples/" + label + ".txt.samples.csv"), "sample source label")

    with Path(str(report) + ".samples.csv").open(encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        require(reader.fieldnames == SAMPLE_HEADER, "raw sample header")
        raw_samples = list(reader)
    expected_rpc_count = row["concurrency"] * row["operations"]
    require(len(raw_samples) == expected_rpc_count == int(values["successfulRpcs"]), "complete raw RPC count")
    freq = int(values["stopwatchFrequency"])
    require(freq > 0, "clock frequency")
    starts, ends, durations = [[] for _ in range(row["concurrency"])], [[] for _ in range(row["concurrency"])], []
    gates = set()
    for sample in raw_samples:
        worker, operation = int(sample["worker"]), int(sample["operation"])
        require(0 <= worker < row["concurrency"] and operation == len(starts[worker]), "worker/operation sequence")
        start, end, ticks = (int(sample[key]) for key in ["start_timestamp", "end_timestamp", "latency_ticks"])
        require(start > 0 and end >= start and ticks == end - start, "raw timestamp consistency")
        require(not ends[worker] or start >= ends[worker][-1], "more than one in-flight RPC in worker")
        gate = start - int((Decimal(sample["start_us_from_gate"]) * freq / 1000000).to_integral_value())
        gates.add(gate)
        close(float(sample["latency_us"]), ticks * 1e6 / freq, "raw duration conversion")
        close(float(sample["end_us_from_gate"]), (end - gate) * 1e6 / freq, "raw end conversion")
        starts[worker].append(start)
        ends[worker].append(end)
        durations.append(ticks)
    require(len(gates) == 1, "inconsistent common measurement gate")
    gate = gates.pop()
    duration_ticks = round(float(values["elapsedSeconds"]) * freq)
    require(min(starts[w][0] for w in range(row["concurrency"])) >= gate
            and max(ends[w][-1] for w in range(row["concurrency"])) <= gate + duration_ticks,
            "raw RPC falls outside common measurement interval")
    ordered = sorted(durations)
    metrics = {}
    for key, percentile in [("p50Microseconds", .5), ("p95Microseconds", .95),
                            ("p99Microseconds", .99), ("p999Microseconds", .999), ("maxMicroseconds", 1)]:
        metrics[key] = ordered[math.ceil(len(ordered) * percentile) - 1] * 1e6 / freq
        close(float(values[key]), metrics[key], "nearest-rank " + key)
    close(float(values["p999SamplesInUpperTail"]), expected_rpc_count / 1000, "nominal upper tail count")
    require(int(values["logicalItems"]) == expected_rpc_count * row["items"], "logical item denominator")
    payload_items = 0 if row["shape"] == "Unary" else row["items"] * (2 if row["shape"] == "Duplex" else 1)
    require(int(values["expectedStreamPayloadItemsPerRpc"]) == payload_items, "stream payload denominator")
    require(int(values["expectedApplicationPayloadBytesPerRpc"]) == row["items"] * 16 * (2 if row["shape"] in ("Unary", "Duplex") else 1),
            "application byte denominator")
    for prefix in ["", "settled"]:
        cap = lambda key: key if not prefix else prefix + key[0].upper() + key[1:]
        seconds = float(values[cap("elapsedSeconds")])
        cpu, allocation = float(values[cap("cpuTotalMicroseconds")]), int(values[cap("allocationBytesTotal")])
        require(seconds > 0 and cpu >= 0 and allocation >= 0, "measurement totals")
        derived = {cap("throughputRpcPerSecond"): expected_rpc_count / seconds,
                   cap("throughputLogicalItemsPerSecond"): expected_rpc_count * row["items"] / seconds,
                   cap("cpuMicrosecondsPerRpc"): cpu / expected_rpc_count,
                   cap("cpuMicrosecondsPerLogicalItem"): cpu / (expected_rpc_count * row["items"]),
                   cap("allocationBytesPerRpc"): allocation / expected_rpc_count,
                   cap("allocationBytesPerLogicalItem"): allocation / (expected_rpc_count * row["items"])}
        for key, value in derived.items():
            close(float(values[key]), value, "total-derived " + key)
        metrics.update(derived)
        metrics[cap("elapsedSeconds")] = seconds
        for key in ["gen0Collections", "gen1Collections", "gen2Collections", "gcPauseTotalMicroseconds"]:
            metrics[cap(key)] = float(values[cap(key)])
    close(float(values["throughputStreamPayloadItemsPerSecond"]), payload_items * metrics["throughputRpcPerSecond"], "stream item rate")
    require(metrics["settledElapsedSeconds"] >= metrics["elapsedSeconds"], "settled precedes completed")
    require(float(values["settledCpuTotalMicroseconds"]) >= float(values["cpuTotalMicroseconds"])
            and int(values["settledAllocationBytesTotal"]) >= int(values["allocationBytesTotal"]), "settled counter regression")
    close(float(values["completedToSettledMicroseconds"]),
          (metrics["settledElapsedSeconds"] - metrics["elapsedSeconds"]) * 1e6, "settlement interval")
    metrics["threadPoolWorkItemsPerRpc"] = int(values["threadPoolCompletedWorkItems"]) / expected_rpc_count
    close(float(values["threadPoolCompletedWorkItemsPerRpc"]), metrics["threadPoolWorkItemsPerRpc"], "ThreadPool denominator")
    with Path(str(report) + ".workers.csv").open(encoding="utf-8-sig", newline="") as handle:
        workers = list(csv.DictReader(handle))
    require(len(workers) == row["concurrency"], "worker count")
    worker_audit = []
    for worker, info in enumerate(workers):
        require(int(info["worker"]) == worker and info["failed"] == "false"
                and int(info["completed"]) == int(info["expected"]) == row["operations"], "worker completion status")
        wdur = [e - s for s, e in zip(starts[worker], ends[worker])]
        for key, expected_value in {
            "first_start_us": (starts[worker][0] - gate) * 1e6 / freq,
            "last_end_us": (ends[worker][-1] - gate) * 1e6 / freq,
            "active_span_us": (ends[worker][-1] - starts[worker][0]) * 1e6 / freq,
            "mean_rpc_latency_us": sum(wdur) * 1e6 / freq / len(wdur),
            "max_rpc_latency_us": max(wdur) * 1e6 / freq,
        }.items():
            close(float(info[key]), expected_value, "worker " + key)
        require(int(info["warmup_completed"]) >= 2 and float(info["warmup_seconds"]) >= 5, "worker warmup minimum")
        worker_audit.append({"worker": worker, "completed": len(wdur), "warmup": int(info["warmup_completed"]),
                             "spanUs": float(info["active_span_us"]), "maxRpcUs": float(info["max_rpc_latency_us"])})
    warmups = [int(x["warmup_completed"]) for x in workers]
    require(sum(warmups) == int(values["actualWarmupRpcs"])
            and min(warmups) == int(values["actualWarmupMinRpcsPerWorker"])
            and max(warmups) == int(values["actualWarmupMaxRpcsPerWorker"]), "warmup aggregate")
    with Path(str(report) + ".fairness.csv").open(encoding="utf-8-sig", newline="") as handle:
        fairness = list(csv.DictReader(handle))
    require(len(fairness) == 6, "fairness cutoff count")
    fairness_audit = []
    for percent, info in zip([10, 25, 50, 75, 90, 100], fairness):
        elapsed = duration_ticks * percent // 100
        counts = [bisect.bisect_right(end, gate + elapsed) for end in ends]
        total = sum(counts)
        squares = sum(n * n for n in counts)
        jain = None if squares == 0 else total * total / (len(counts) * squares)
        require([int(v) for v in info["per_worker_completions"].split(";")] == counts, "fairness raw counts")
        require(int(info["min_completions"]) == min(counts) and int(info["max_completions"]) == max(counts)
                and int(info["total_completions"]) == total, "fairness aggregates")
        close(float(info["fraction"]), percent / 100, "fairness fraction")
        close(float(info["elapsed_us"]), elapsed * 1e6 / freq, "fairness interval")
        if jain is None:
            require(info["jain_index"] == "N/A", "empty fairness must be N/A")
        else:
            close(float(info["jain_index"]), jain, "Jain index")
        fairness_audit.append({"percent": percent, "min": min(counts), "max": max(counts), "total": total, "jain": jain})
    return {**row, "status": "Passed", "exitCode": 0, "successfulRpcs": expected_rpc_count,
            "rawSampleCount": len(raw_samples), "completedBoundaryAvailable": True, "settledBoundaryAvailable": True,
            "metrics": metrics, "pathCounters": {k: int(values[k]) for k in COUNTERS},
            "fairness": fairness_audit, "workers": worker_audit,
            "identity": {k: values[k] for k in [*IDENTITY_FIELDS.values(), "machine", "runtime", "builtForNativeAot",
                         "dynamicCodeSupported", "dynamicCodeCompiled", "measuredBinarySha256", "physicalSessionId"]},
            "runtimeAssemblySha256": values.get("runtimeAssemblySha256"),
            "reportSha256": sha(report.read_bytes()),
            "rawSamplesSha256": sha(Path(str(report) + ".samples.csv").read_bytes())}


def grouped_comparisons(rows):
    comparisons = []
    metrics = ["throughputRpcPerSecond", "cpuMicrosecondsPerRpc", "allocationBytesPerRpc",
               "settledThroughputRpcPerSecond", "settledCpuMicrosecondsPerRpc", "settledAllocationBytesPerRpc",
               "p50Microseconds", "p95Microseconds", "p99Microseconds", "p999Microseconds", "maxMicroseconds",
               "threadPoolWorkItemsPerRpc"]
    for kind in ["AB", "AA"]:
        cells = sorted({(r["transport"], r["shape"], r["items"], r["bytes"], r["concurrency"]) for r in rows if r["kind"] == kind})
        for cell in cells:
            members = [r for r in rows if r["kind"] == kind and
                       (r["transport"], r["shape"], r["items"], r["bytes"], r["concurrency"]) == cell]
            pairs = []
            for repetition in [0, 1]:
                current = [r for r in members if r["round"] == repetition]
                groups = [[r for r in current if r["position"] in (lo, lo + 1)] for lo in [0, 2]] if kind == "AB" else [current]
                for group in groups:
                    left = next(r for r in group if r.get("arm") == "A") if kind == "AB" else next(r for r in group if r["side"] == "left")
                    right = next(r for r in group if r.get("arm") == "B") if kind == "AB" else next(r for r in group if r["side"] == "right")
                    pair = {"round": repetition, "left": left["name"], "right": right["name"],
                            "leftStatus": left["status"], "rightStatus": right["status"], "changesPct": None}
                    if left["status"] == right["status"] == "Passed":
                        pair["changesPct"] = {m: (right["metrics"][m] / left["metrics"][m] - 1) * 100 for m in metrics}
                        pair["allocationBytesPerRpcDifference"] = right["metrics"]["allocationBytesPerRpc"] - left["metrics"]["allocationBytesPerRpc"]
                    pairs.append(pair)
            valid = [p for p in pairs if p["changesPct"] is not None]
            aggregate = {}
            for metric in metrics:
                deltas = [p["changesPct"][metric] for p in valid]
                aggregate[metric] = None if not deltas else {
                    "geometricMeanChangePct": math.expm1(statistics.mean(math.log1p(d / 100) for d in deltas)) * 100,
                    "medianPairChangePct": statistics.median(deltas),
                    "minPairChangePct": min(deltas), "maxPairChangePct": max(deltas), "pairs": deltas}
            arm_metrics = {}
            for arm in (["A", "B"] if kind == "AB" else ["left", "right"]):
                values = [r for r in members if r["status"] == "Passed" and r["arm" if kind == "AB" else "side"] == arm]
                arm_metrics[arm] = {m: None if not values else statistics.median(r["metrics"][m] for r in values) for m in metrics}
            comparisons.append({"kind": kind, "transport": cell[0], "shape": cell[1], "items": cell[2], "bytes": cell[3],
                                "concurrency": cell[4], "plannedPairs": 4 if kind == "AB" else 2, "validPairs": len(valid),
                                "pairs": pairs, "summary": aggregate, "armMedians": arm_metrics})
    return comparisons


def main():
    script_sha256 = sha(Path(__file__).read_bytes())
    parser = argparse.ArgumentParser()
    parser.add_argument("population_dir", type=Path, nargs="?", help="Legacy positional alias for --input")
    parser.add_argument("--input", type=Path, help="Evidence population directory containing artifact-metadata.json and raw/")
    parser.add_argument("--output", type=Path, help="Directory for independent results; defaults to --input")
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--archive", type=Path, help="Same artifact's preserved verified ZIP; default population_dir/rpc-screen.zip")
    args = parser.parse_args()
    if (args.input is None) == (args.population_dir is None):
        parser.error("Supply exactly one of --input or the legacy positional population_dir")
    input_folder = (args.input or args.population_dir).resolve()
    folder = args.output.resolve() if args.output else input_folder
    folder.mkdir(parents=True, exist_ok=True)
    repo = args.repo.resolve()
    raw = input_folder / "raw"
    metadata = load(input_folder / "artifact-metadata.json")
    archive_path = args.archive.resolve() if args.archive else input_folder / "rpc-screen.zip"
    archive = archive_path.read_bytes()
    require(sha(archive) == metadata["github"]["digest"].removeprefix("sha256:")
            and len(archive) == metadata["github"]["size_in_bytes"], "GitHub original ZIP integrity")
    require(metadata["github"]["workflow_run"]["head_sha"] == CANDIDATE
            and metadata["github"]["workflow_run"]["id"] == 38173700315, "GitHub artifact source")
    inputs = []
    with zipfile.ZipFile(io.BytesIO(archive)) as zf:
        require(zf.testzip() is None, "ZIP CRC failure")
        seen = set()
        for entry in zf.infolist():
            if entry.is_dir():
                continue
            name = entry.filename
            require(name not in seen and not PurePosixPath(name).is_absolute() and ".." not in PurePosixPath(name).parts,
                    "duplicate/unsafe ZIP path")
            seen.add(name)
            content = zf.read(entry)
            require((raw / name).read_bytes() == content, "extracted file differs from original ZIP: " + name)
            inputs.append({"path": name, "bytes": len(content), "sha256": sha(content)})
    plan = load(raw / "screen-plan.json")
    population = plan["runtimePopulation"]
    require(population in ("jit-pgo0", "jit-pgo1", "nativeaot") and metadata["github"]["name"] ==
            f"issue735-b2-rpc-{population}-byte-array-{CANDIDATE}-attempt1", "population/artifact label")
    require(plan["payloadKind"] == "byte-array" and plan["cases"] == [list(c) for c in CASES]
            and plan["aaCases"] == [list(CASES[1])] and plan["abOrders"] == [list("ABBA"), list("BAAB")]
            and plan["cpuAffinity"] == [0, 1, 2, 3] and plan["grantQuantumBytes"] == 4096,
            "predeclared workload and order")
    require(plan["baselineSha"] == BASE and plan["correctnessControlSha"] == BASE
            and plan["initialDevBaselineSha"] == INITIAL and plan["candidateSha"] == CANDIDATE
            and plan["measurementRecipeSha"] == CANDIDATE, "plan source provenance")
    identities, source_manifests = validate_sources(repo, raw, plan)
    binary_data = {p.relative_to(raw).as_posix(): p.read_bytes() for p in (raw / "binaries").rglob("*") if p.is_file()}
    binary_manifest = [{"path": p, "bytes": len(data), "sha256": sha(data)} for p, data in sorted(binary_data.items())]
    for arm in "AB":
        subject = f"binaries/{arm}/SharpLink.Benchmarks" + ("" if population == "nativeaot" else ".dll")
        require(sha(binary_data[subject]) == plan["sourceTrees"][arm]["binarySha256"], "post-build plan binary hash")
    exits = load(raw / "exits.json")
    require(len({e["name"] for e in exits}) == len(exits), "duplicate process label/retry")
    steps = {e["name"]: e for e in exits}
    expected = schedule()
    allowed_rpc = {r["name"] for r in expected}
    actual_rpc_steps = [e for e in exits if e["name"].startswith(("ab-", "aa-"))]
    require(all(e["name"] in allowed_rpc for e in actual_rpc_steps), "unplanned process")
    recorded_names = [e["name"] for e in actual_rpc_steps]
    require(recorded_names == [r["name"] for r in expected if r["name"] in recorded_names], "actual AB/BA process order")
    for previous, following in zip(actual_rpc_steps, actual_rpc_steps[1:]):
        require(previous["utcStartEpochSeconds"] + previous["wallSeconds"]
                <= following["utcStartEpochSeconds"] + 0.001, "overlapping supposedly sequential matched processes")
    for arm in "AB":
        build = steps.get("build-" + arm)
        if build and build.get("exitCode") == 0:
            require(f"-p:FlowEvidenceB2={str(arm == 'B').lower()}" in build["command"], "compiled diagnostics mode")
            text = (raw / f"build-{arm}.stdout.log").read_text()
            if population == "nativeaot":
                require("publish" in build["command"] and "-p:PublishAot=true" in build["command"]
                        and "Generating native code" in text, "actual NativeAOT publication")
            else:
                require("build" in build["command"] and "-p:PublishAot=false" in build["command"]
                        and "Build succeeded." in text, "JIT build")
    rows = []
    for scheduled in expected:
        name = scheduled["name"]
        step = steps.get(name)
        fallback = {**scheduled, "status": "NotRun", "exitCode": None, "metrics": None,
                    "completedBoundaryAvailable": False, "settledBoundaryAvailable": False,
                    "successfulRpcs": None, "pathCounters": None}
        if step is None:
            rows.append(fallback)
        elif step.get("exitCode") != 0:
            report = raw / "samples" / (name + ".txt")
            values = kv(report) if report.exists() else {}
            rows.append({**fallback, "status": "Failed", "exitCode": step.get("exitCode"),
                         "reportedStatus": values.get("status"),
                         "failureFiles": [p.relative_to(raw).as_posix() for p in (raw / "samples").glob(name + "*")]})
        else:
            try:
                rows.append(validate_sample(raw, scheduled, step, identities, population, binary_data))
            except Exception as error:
                rows.append({**fallback, "status": "InvalidEvidence", "exitCode": 0,
                             "validationError": f"{type(error).__name__}: {error}"})
    comparisons = grouped_comparisons(rows)
    passed = [r for r in rows if r["status"] == "Passed"]
    summary = {
        "schema": "independent-byte-array-5cab-raw-audit-v1", "population": population,
        "artifactId": metadata["github"]["id"], "originalZipSha256": sha(archive), "originalZipBytes": len(archive),
        "verifiedArchiveFile": str(archive_path),
        "originalZipFilesVerified": len(inputs), "scriptSha256": script_sha256,
        "statusCounts": {s: sum(r["status"] == s for r in rows) for s in ["Passed", "Failed", "NotRun", "InvalidEvidence"]},
        "plannedProcesses": 72, "actualProcessRecords": len(actual_rpc_steps), "allExitRecords": len(exits),
        "nonzeroExitRecords": [{"name": e["name"], "exitCode": e.get("exitCode")} for e in exits if e.get("exitCode") != 0],
        "actualProcessesChronologicallyNonoverlapping": True,
        "rawRpcRowsIndependentlyRecomputed": sum(r["rawSampleCount"] for r in passed),
        "compiledSourcesChecked": {a: len(m) for a, m in source_manifests.items()},
        "measurementInputsCheckedPerArm": len(identities["A"]["measurementSourceManifest"]),
        "measurementSourceSha": identities["A"]["MeasurementSourceSha"],
        "binaryFilesHashed": len(binary_manifest),
        "allEightCountersZeroForValidatedProcesses": all(all(v == 0 for v in r["pathCounters"].values()) for r in passed),
        "allEightCountersZeroForAll72Proven": len(passed) == 72 and all(all(v == 0 for v in r["pathCounters"].values()) for r in passed),
        "abCells": sum(c["kind"] == "AB" for c in comparisons), "aaCells": sum(c["kind"] == "AA" for c in comparisons),
        "abValidPairs": sum(c["validPairs"] for c in comparisons if c["kind"] == "AB"),
        "aaValidPairs": sum(c["validPairs"] for c in comparisons if c["kind"] == "AA"),
        "derivation": "original per-process integer totals plus complete per-RPC ticks/worker/fairness CSV; no summary metrics as input",
        "relativeChangeConvention": "AB=(B/A-1)*100; AA=(right/left-1)*100; report median and geometric mean within same cell/population",
        "limitations": ["No cross-runner or DTO absolute-metric pooling.", "Byte-array codec is unsized; these runs never activate a B2 lease.",
                        "Observed call/stream settlement is not proof that all credit, SendPump or late WU has settled.",
                        "Jain measures RPC completion distribution, not stream-credit scheduler fairness.",
                        "N/1000 is nominal tail size; no effective sample-size correction or tail-confidence claim.",
                        "CPU/allocation process totals include client, server and payload validation; not controller-only costs.",
                        "A native ELF and executed hash prove an actual AOT run; source provenance relies additionally on saved build commands/logs and compiled reports."]}
    require(script_sha256 == sha(Path(__file__).read_bytes()), "audit source changed during execution")
    save(folder / "independent-verification.json", summary)
    save(folder / "independent-processes.json", rows)
    save(folder / "independent-paired-comparisons.json", comparisons)
    save(folder / "independent-source-and-binary-manifest.json", {"production": source_manifests, "binaries": binary_manifest,
                                                                 "identities": identities})
    save(folder / "independent-original-file-inventory.json", inputs)
    lines = [f"# {population} byte-array：原始 RPC 独立复算", "", f"Artifact `{summary['artifactId']}`；原 ZIP SHA256 `{summary['originalZipSha256']}`。",
             "", f"状态：`{summary['statusCounts']}`。以下每行只属于这个 artifact 的同一 cell；正数表示 B/A 增加，CPU/分配增加是成本上涨。",
             "", "| kind | transport | shape | pair 数 | RPC/s Δ% | CPU/RPC Δ% | B/RPC Δ% | settled RPC/s Δ% | P99 Δ% | P99.9 Δ% |", "|---|---|---|---:|---:|---:|---:|---:|---:|---:|"]
    keys = ["throughputRpcPerSecond", "cpuMicrosecondsPerRpc", "allocationBytesPerRpc", "settledThroughputRpcPerSecond", "p99Microseconds", "p999Microseconds"]
    for c in comparisons:
        formatted = ["N/A" if c["summary"][k] is None else f"{c['summary'][k]['medianPairChangePct']:+.3f}" for k in keys]
        lines.append(f"| {c['kind']} | {c['transport']} | {c['shape']} | {c['validPairs']}/{c['plannedPairs']} | " + " | ".join(formatted) + " |")
    lines += ["", "AB 每 cell 四个相邻 pair，分别来自 ABBA 与 BAAB；AA 固定 right/left，第二轮交换实际执行顺序。JSON 保留每个 pair 的原标签、全部比值、min/max、几何均值及中位数；表格只展示同 cell 配对差值的中位数。",
              "", "八项 B2 counter 必须逐进程明示为 0；缺失或失败的样本不会被填零。字节数组未启用 B2，故表中差异不能称为 B2 grant 的正面收益证据。",
              "", "原始窗口/Flush/codec/身份/二进制、逐 RPC timestamp、nearest-rank 尾延迟、worker 顺序和六时点 Jain 均独立核验。机器名相同不构成跨 job 同机证据。",
              "", "此审计不导入 root strict parser，不解析 paired-summary.json；原 ZIP 与解压原件均不写入。"]
    (folder / "independent-comparison-table.zh-CN.md").write_text("\n".join(lines) + "\n")
    print(json.dumps(summary, ensure_ascii=False))
    for row in rows:
        if row["status"] == "InvalidEvidence":
            print(json.dumps({"invalid": row["name"], "reason": row["validationError"]}, ensure_ascii=False))
    return 0 if all(r["status"] == "Passed" for r in rows) else 2


if __name__ == "__main__":
    raise SystemExit(main())
