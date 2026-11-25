#!/usr/bin/env python3
"""Freeze a short-stream cell and grade complete same-host report pairs."""

import argparse
import json
from pathlib import Path

from d6_paired_steady import AB_PAIRS, HOST_FIELDS, METRICS, RECORDER, SCHEMA, evaluate, matrix, sample_names


OPERATIONS = ("c2s", "s2c", "duplex")
PHASES = {"first_progress": "FirstProgress", "business": "BusinessComplete", "owners": "ApplicationOwnersExited"}
PRECISION = dict(METRICS)
for phase in ("first_progress", "business"):
    PRECISION[f"{phase}_p50_us"] = 0.03
    PRECISION[f"{phase}_p99_us"] = 0.05
CRITICAL = ("p99_us", "first_progress_p99_us", "business_p99_us", "cpu_us_per_op")
MAXIMUM_SAMPLES = 2_000_000


def stream_plan(transport, concurrency, operation, items):
    matrix.require(operation in OPERATIONS and items in (32, 256), "unregistered shape or item count")
    matrix.require(transport in ("tcp", "sharedmemory") and concurrency in (1, 8), "unregistered transport or concurrency")
    # One cell per host. 24 trials; even 91+11 seconds leaves time for
    # restores and upload within our unchanged 60-minute hard job limit.
    duration = 61 if items == 32 else 91
    return dict(
        transport=transport, concurrency=concurrency, operation=operation, items=items, minimum_completed=100_000,
        arguments={"mode": "local", "transport": transport, "operation": operation, "concurrency": concurrency,
                   "stream-size": items, "profile": "balanced", "min-connections": 1, "max-connections": 1,
                   "bind-ip": "127.0.0.1", "short-stream-phases": "true",
                   "maximum-recorded-operations": MAXIMUM_SAMPLES, "recording": "formal", "warmup": 11,
                   "duration": duration, "drain-timeout": 5, "heartbeat-interval": 120,
                   "heartbeat-check-interval": 120, "heartbeat-timeout": 600},
        aa=["PP"] * 4, ab=list(AB_PAIRS), aa_maximum_deviation=PRECISION, critical=CRITICAL)


def read_short_stream(path, arm, source, cell, hosts):
    report = matrix.read_json(path)
    matrix.require(report["Workload"] == "SharpLink.StreamLoadTest" and report["SchemaVersion"] == SCHEMA,
                   "report schema/workload mismatch")
    matrix.require(report["SourceCommit"] == source, f"{arm} report source differs from the frozen arm")
    matrix.require(report["Runtime"].startswith(".NET 10."), "the paired short-stream comparison uses .NET 10")
    cfg = report["Configuration"]
    for key, expected in {
        "Mode": 0, "Transport": matrix.TRANSPORTS[cell["transport"]], "Operation": cell["operation"],
        "ConcurrencyConfig": [cell["concurrency"]], "StreamSize": cell["items"], "ShortStreamPhases": True,
        "VerifyShortStreams": False, "PerformanceProfile": 0, "RecordingMode": 1, "MinConnections": 1,
        "MaxConnections": 1, "WarmupSeconds": 11, "DurationSeconds": cell["arguments"]["duration"],
        "DrainTimeoutSeconds": 5, "MaximumRecordedOperations": MAXIMUM_SAMPLES, "BindIp": "127.0.0.1",
        "DetailedSharedMemoryEvidence": False, "ConsumerDelayMilliseconds": 0, "EarlyBreakAfter": 0,
        "PauseAfter": 0, "PauseMilliseconds": 0, "HeartbeatIntervalSeconds": 120,
        "HeartbeatCheckIntervalSeconds": 120, "HeartbeatTimeoutSeconds": 600,
    }.items():
        matrix.require(cfg[key] == expected, f"{path.name}: unexpected {key}")
    matrix.require(cfg.get("MaxSendQueueBytes") is None, "study uses profile-default queue")
    stage, = report["Results"]
    matrix.validate_result(stage, {"recorderVersion": RECORDER})
    matrix.require((stage["Operation"], stage["Concurrency"], stage["WorkerCount"]) ==
                   (cell["operation"], cell["concurrency"], cell["concurrency"]), "wrong cell")
    n = stage["Success"]
    matrix.require(n >= cell["minimum_completed"] and stage["MeasurementDurationSeconds"] >= cell["arguments"]["duration"] - 1 and
                   stage["WarmupDurationSeconds"] >= 10, "insufficient completions or measured time")
    matrix.require(stage["MaximumSampleCapacity"] == MAXIMUM_SAMPLES and stage["ValidatedMessages"] == n * cell["items"],
                   "the formal run did not validate the exact stream item count")
    identity = tuple(report[field] for field in HOST_FIELDS) + (stage["StopwatchFrequency"],)
    matrix.require(all(x not in ("", "unknown", None) for x in identity), "missing report environment")
    hosts.add(identity)
    matrix.require(len(hosts) == 1, "report runtime/host settings differ")

    phases = stage["ShortStreamPhases"]
    first = "first input pull" if cell["operation"] == "c2s" else "first response item"
    matrix.require(phases["FirstProgressMeaning"] == first, "different application start boundary")
    values = {}
    for phase, field in PHASES.items():
        stats = phases[field]
        matrix.require(stats["Count"] == n, f"{phase}: phase population differs from the complete RPCs")
        for k, attr in (("p50", "P50Us"), ("p99", "P99Us"), ("p999", "P999Us")):
            values[f"{phase}_{k}_us"] = matrix.number(stats[attr], f"{phase} {k}", positive=True)
    for k, attr in (("p50", "P50Us"), ("p99", "P99Us"), ("p999", "P999Us")):
        matrix.require(values[f"first_progress_{k}_us"] <= values[f"business_{k}_us"] <= values[f"owners_{k}_us"],
                       "phase order is not the observed application timeline")
        matrix.require(stage[attr] == values[f"owners_{k}_us"], "formal latency does not use the same owner-exit population")
    evidence = stage["Evidence"]
    return dict(**values, file=path.name, arm=arm, completed=n, items=stage["ValidatedMessages"],
                measured_s=stage["MeasurementDurationSeconds"], p50_us=values["owners_p50_us"],
                p99_us=values["owners_p99_us"], p999_us=values["owners_p999_us"],
                ops_per_s=matrix.number(stage["Qps"], "streams/s", positive=True),
                cpu_us_per_op=matrix.number(evidence["CpuMilliseconds"], "CPU", positive=True) * 1000 / n,
                managed_b_per_op=matrix.number(evidence["AllocatedBytes"], "managed bytes") / n,
                gen0=evidence["Gen0Collections"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=OPERATIONS)
    parser.add_argument("transport", choices=("tcp", "sharedmemory"))
    parser.add_argument("concurrency", type=int, choices=(1, 8))
    parser.add_argument("items", type=int, choices=(32, 256))
    parser.add_argument("--reports", type=Path, help="omit for a no-timing plan")
    parser.add_argument("--baseline")
    parser.add_argument("--candidate")
    args = parser.parse_args()
    cell = stream_plan(args.transport, args.concurrency, args.operation, args.items)
    if args.reports:
        if not args.baseline:
            parser.error("--baseline is required with --reports")
        result = evaluate(args.reports, cell, args.baseline, args.candidate, reader=read_short_stream)
    else:
        result = dict(cell=cell, files={phase: sample_names(phase, cell[phase]) for phase in ("aa", "ab")})
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
