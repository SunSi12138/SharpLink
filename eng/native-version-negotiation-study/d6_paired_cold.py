#!/usr/bin/env python3
"""Freeze matched fresh-Session first RPCs without confusing startup and Prepare."""

import argparse
import json
import math
from pathlib import Path

from d6_paired_steady import AB_PAIRS, HOST_FIELDS, SCHEMA, evaluate, matrix, sample_names


FRESH_PER_TRIAL = 12_500
TOTAL_PER_ARM = FRESH_PER_TRIAL * len(AB_PAIRS)
PHASES = {
    "connect": "ConnectTicks", "local_handle": "LocalHandleTicks",
    "prepare": "PrepareTicks", "first_rpc": "FirstRpcTicks",
    "connect_to_first_success": "ConnectToFirstSuccessTicks",
}


def cold_plan(transport):
    matrix.require(transport in ("tcp", "sharedmemory"), "unregistered cold transport")
    matrix.require(TOTAL_PER_ARM >= 100_000, "insufficient planned fresh-session observations per arm")
    return dict(transport=transport, concurrency=1, operation="cold-unary32",
                fresh_sessions_per_trial=FRESH_PER_TRIAL, fresh_sessions_per_measured_arm=TOTAL_PER_ARM,
                arguments={"mode": "local", "transport": transport, "operation": "unary32", "concurrency": 1,
                           "bind-ip": "127.0.0.1", "cold-first-rpc-samples": FRESH_PER_TRIAL + 1,
                           "payload-size": 32, "profile": "balanced", "min-connections": 1, "max-connections": 1,
                           "payload-pattern": "compressible", "request-timeout": "default", "admission": "disabled",
                           "metrics-port": 0, "warmup": 0,
                           "heartbeat-interval": 120, "heartbeat-check-interval": 120, "heartbeat-timeout": 600},
                aa=["PP"] * 4, ab=list(AB_PAIRS), aa_maximum_deviation={"p50_us": 0.03, "p99_us": 0.05},
                critical=("p99_us",), extras=("p999_us",), compare_allocations=False)


def percentiles(values, scale):
    values = sorted(values)
    return {f"p{label}_us": values[math.ceil(percent * len(values)) - 1] * scale
            for label, percent in (("50", 0.5), ("99", 0.99), ("999", 0.999))}


def read_cold(path, arm, source, cell, hosts):
    report = matrix.read_json(path)
    matrix.require(report["Workload"] == "SharpLink.LoadTest.ColdFirstRpc" and report["SchemaVersion"] == SCHEMA,
                   "cold report schema/workload mismatch")
    matrix.require(report["SourceCommit"] == source, f"{arm}: reported source differs from the frozen arm")
    matrix.require(report["Runtime"].startswith(".NET 10."), "the paired cold comparison uses .NET 10")
    cfg = report["Configuration"]
    for key, expected in {
        "Mode": 0, "Transport": matrix.TRANSPORTS[cell["transport"]], "Operation": "unary32", "PayloadSize": 32,
        "PayloadPattern": "compressible", "PerformanceProfile": 0, "ConcurrencyConfig": [1], "MinConnections": 1,
        "MaxConnections": 1, "BindIp": "127.0.0.1", "MetricsPort": 0, "WarmupSeconds": 0,
        "ColdFirstRpcSamples": cell["fresh_sessions_per_trial"] + 1, "RequestTimeoutMode": "default",
        "AdmissionMode": "disabled", "DetailedSharedMemoryEvidence": False, "TailObserver": False,
        "UseStaticEndpoints": False, "UseDynamicResolver": False, "HeartbeatIntervalSeconds": 120,
        "HeartbeatCheckIntervalSeconds": 120, "HeartbeatTimeoutSeconds": 600,
    }.items():
        matrix.require(cfg[key] == expected, f"{path.name}: unexpected {key}")
    matrix.require(cfg.get("MaxSendQueueBytes") is None, "study uses the profile-default queue")
    samples = report["Results"]
    matrix.require(len(samples) == cell["fresh_sessions_per_trial"] + 1, "partial cold trial")
    frequency = samples[0]["StopwatchFrequency"]
    matrix.require(type(frequency) is int and frequency > 0, "invalid cold stopwatch frequency")
    identity = tuple(report[field] for field in HOST_FIELDS) + (frequency,)
    matrix.require(all(x not in ("", "unknown", None) for x in identity), "missing report environment")
    hosts.add(identity)
    matrix.require(len(hosts) == 1, "report runtime/host settings differ")

    fresh = {}
    startup = {}
    for index, sample in enumerate(samples, 1):
        matrix.require(type(sample["Index"]) is int and sample["Index"] == index and
                       sample["FirstInProcess"] is (index == 1), "cold trial must have exactly one initial sample")
        matrix.require(sample["StopwatchFrequency"] == frequency, "stopwatch frequency changed during cold trial")
        ticks = {}
        for label, field in PHASES.items():
            val = sample.get(field)
            if label == "prepare" and arm == "P":
                matrix.require(val is None, "baseline has no explicit Prepare; do not encode zero")
                continue
            matrix.require(type(val) is int and (val >= 0 if label == "local_handle" else val > 0),
                           f"invalid timestamp delta: {label}")
            ticks[label] = val
        matrix.require(sum(v for k, v in ticks.items() if k != "connect_to_first_success") <=
                       ticks["connect_to_first_success"], "subphases do not fit in the shared interval")
        if index == 1:
            startup = {f"{k}_us": v * 1_000_000.0 / frequency for k, v in ticks.items()}
        else:
            for key, val in ticks.items():
                fresh.setdefault(key, []).append(val)

    phases = {f"{name}_us": percentiles(values, 1_000_000.0 / frequency) for name, values in fresh.items()}
    return dict(**phases["connect_to_first_success_us"], file=path.name, arm=arm,
                completed=len(samples) - 1, first_in_process_us=startup, fresh_session_metrics=phases)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("transport", choices=("tcp", "sharedmemory"))
    parser.add_argument("--reports", type=Path, help="omit for the no-timing plan")
    parser.add_argument("--baseline")
    parser.add_argument("--candidate")
    args = parser.parse_args()
    cell = cold_plan(args.transport)
    if args.reports:
        if not args.baseline:
            parser.error("--baseline is required with --reports")
        result = evaluate(args.reports, cell, args.baseline, args.candidate, reader=read_cold)
    else:
        result = dict(cell=cell, files={phase: sample_names(phase, cell[phase]) for phase in ("aa", "ab")},
                      scope="Fresh-Session across eight ordered trials per arm; first process startup is raw only.")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
