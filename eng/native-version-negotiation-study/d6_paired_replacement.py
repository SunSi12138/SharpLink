#!/usr/bin/env python3
"""Validate exact A/B business samples and real same-Session replacement intervals."""

import gzip
import re
import statistics
import struct

from d6_paired_cold import percentiles
from d6_paired_steady import AB_PAIRS, HOST_FIELDS, matrix

MIN_CALLS = 100_000
MAX_CALLS = 2_097_152
CALL = struct.Struct("<qq")
TEST_PREFIX = "QualifiedReplacementPreservesAnUnrelatedServiceOnOnePhysicalSession"
DYNAMIC_TEST_PREFIX = "QualifiedDynamicShortStreamsUseThePreparedRoute"
TRANSPORT = {"tcp": ("tcp", "Tcp"), "sharedmemory": ("shared-memory", "SharedMemory")}
UPDATE_FIELDS = ("UpdateStartedTicks", "LoadCompleteTicks", "PublicCompleteTicks",
                 "FirstNewCallStartedTicks", "FirstNewCallCompleteTicks")


def replace_plan(transport, cadence, interceptor="none"):
    matrix.require(transport in TRANSPORT and cadence in (0, 100, 10000), "unregistered replacement cell")
    matrix.require(interceptor in ("none", "forward") and (cadence == 0 or interceptor == "none"),
                   "the forwarding interceptor is a separate steady dynamic/static cell")
    return dict(transport=transport, concurrency=1, operation=f"replacement-{cadence}-{interceptor}",
                cadence=cadence, interceptor=interceptor,
                arguments={}, aa=["PP"] * 4, ab=list(AB_PAIRS),
                aa_maximum_deviation={"a_p50_us": .03, "a_p99_us": .05,
                                      "b_p50_us": .03, "b_p99_us": .05, "cpu_us_per_op": .03},
                critical=("a_p99_us", "b_p99_us", "cpu_us_per_op"),
                extras=("a_ops_per_s", "b_ops_per_s", "a_p999_us", "b_p999_us") +
                       (("update_public_p50_us", "update_to_first_r2_p50_us") if cadence else ()),
                compare_allocations=cadence == 0)


def invocation(transport, *, measured, dynamic_streams=False):
    _, suffix = TRANSPORT[transport]
    prefix = DYNAMIC_TEST_PREFIX if dynamic_streams else TEST_PREFIX
    timeout = ("130s" if dynamic_streams else "110s") if measured else "45s"
    return ["--treenode-filter", "/*/*/RuntimeAssemblyIntegrationTests/" + prefix + suffix,
            "--minimum-expected-tests", "1", "--zero-tests-policy", "strict", "--maximum-parallel-tests", "1",
            "--timeout", timeout, "--output", "Detailed", "--no-ansi"]


def verify_no_tracing(log):
    matrix.require(log.splitlines().count("REPLACE_NO_RPC_TRACING") == 1,
                   "replacement workload did not qualify with RPC tracing disabled")


def verify_dynamic_preflight(log, transport, interceptor, exit_code):
    name, _ = TRANSPORT[transport]
    expected = [f"DYNAMIC_STREAM_VERIFIED transport={name} family={shape} items={size} interceptor={interceptor}"
                for size in (32, 256) for shape in ("c2s", "s2c", "duplex")]
    lines = [re.sub(r"\x1b\[[0-9;]*m", "", line).strip() for line in log.splitlines()]
    matrix.require(exit_code == 0 and [line for line in lines if line.startswith("DYNAMIC_STREAM_VERIFIED ")] == expected,
                   "incomplete real dynamic short-stream preflight")


def read_calls(path, count, elapsed, update_probes=None, windows=()):
    try:
        with gzip.open(path, "rb") as archive:
            raw = archive.read(count * CALL.size + 1)
    except EOFError as error:
        raise ValueError(f"{path.name}: incomplete compressed trace") from error
    matrix.require(len(raw) == count * CALL.size, f"{path.name}: partial or extra business samples")
    durations, overlap = [], []
    window, previous = 0, 0
    for index, (start, end) in enumerate(CALL.iter_unpack(raw)):
        matrix.require(previous <= start < end <= elapsed, f"{path.name}: unordered call timestamps")
        if update_probes is not None and index in update_probes:
            matrix.require((start, end) == update_probes[index], "R2 first verified call is not in the A population")
        previous = end
        duration = end - start
        durations.append(duration)
        while window < len(windows) and windows[window][1] <= start:
            window += 1
        if window < len(windows) and windows[window][0] < end:
            overlap.append(duration)
    return durations, overlap


def read_replacement(path, arm, source, cell, hosts):
    report = matrix.read_json(path)
    matrix.require(report["Workload"] == "SharpLink.IntegrationTests.QualifiedReplacement"
                   and report["SchemaVersion"] == 2 and report["SourceCommit"] == source, "replacement identity mismatch")
    matrix.require(report["RpcTracingEnabled"] is False, "replacement report included RPC tracing")
    cadence = cell["cadence"]
    matrix.require(report["Transport"] == TRANSPORT[cell["transport"]][0] and report["Cadence"] == cadence
                   and report["WarmupSeconds"] == 11 and report["MeasureSeconds"] == 21
                   and report["Runtime"].startswith(".NET 10."), "replacement configuration mismatch")
    matrix.require(report["Interceptor"] == cell["interceptor"], "replacement interceptor mismatch")
    frequency, elapsed = report["StopwatchFrequency"], report["ElapsedTicks"]
    cpu, allocation, gen0 = report["CpuTimeTicks"], report["ManagedAllocatedBytes"], report["Gen0"]
    matrix.require(type(frequency) is int and frequency > 0 and type(elapsed) is int
                   and 21 * frequency <= elapsed <= 110 * frequency and type(cpu) is int and cpu > 0
                   and type(allocation) is int and allocation >= 0 and type(gen0) is int and gen0 >= 0,
                   "replacement timer or process counters invalid")
    identity = tuple(report[field] for field in HOST_FIELDS) + (frequency,)
    matrix.require(all(value not in ("", "unknown", None) for value in identity), "replacement host identity incomplete")
    hosts.add(identity)
    matrix.require(len(hosts) == 1, "replacement host, runtime or counter identity differs")

    a, b = report["CompletedA"], report["CompletedB"]
    matrix.require(all(type(count) is int and MIN_CALLS <= count <= MAX_CALLS for count in (a, b)),
                   "replacement A/B completion counts out of bounds")
    expected = range(cadence, a, cadence) if cadence else ()
    replacements = report["Replacements"]
    matrix.require(len(replacements) == len(expected), "replacement frequency did not reach the frozen cadence")
    probes, windows, publish, first_r2 = {}, [], [], []
    previous = 0
    for count, entry in zip(expected, replacements):
        t = [entry[field] for field in UPDATE_FIELDS]
        matrix.require(entry["CompletedBefore"] == count and all(type(val) is int for val in t) and
                       previous <= t[0] <= t[1] < t[2] <= t[3] < t[4] <= elapsed,
                       "replacement events are incomplete, overlapping, or out of sequence")
        probes[count] = (t[3], t[4])
        windows.append((t[1], t[4]))
        publish.append(t[2] - t[1])
        first_r2.append(t[4] - t[1])
        previous = t[4]

    a_values, _ = read_calls(path.with_suffix(".a.bin.gz"), a, elapsed, update_probes=probes)
    b_values, b_overlap = read_calls(path.with_suffix(".b.bin.gz"), b, elapsed, windows=windows)
    scale = 1_000_000 / frequency
    stats = {f"{service}_{key}": value for service, durations in (("a", a_values), ("b", b_values))
             for key, value in percentiles(durations, scale).items()}
    stats.update(arm=arm, a_completed=a, b_completed=b, a_ops_per_s=a * frequency / elapsed,
                 b_ops_per_s=b * frequency / elapsed, replacements=len(probes), b_update_overlap=len(b_overlap),
                 cpu_us_per_op=cpu / (10 * (a + b)), managed_b_per_op=allocation / (a + b), gen0=gen0)
    if cadence:
        matrix.require(bool(b_overlap), "no genuine B probe overlapped a public replacement through first R2")
        stats.update(update_public_p50_us=statistics.median(publish) * scale,
                     update_to_first_r2_p50_us=statistics.median(first_r2) * scale)
    # A sparse B-on-update population is diagnostic only; never substitute it for the whole-phase guard.
    if len(b_overlap) >= MIN_CALLS:
        stats["b_update_overlap_latency"] = percentiles(b_overlap, scale)
    return stats
