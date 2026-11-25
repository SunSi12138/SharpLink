"""Grade one JIT dynamic stream stratum from raw, same-population phase samples."""

import gzip
import struct

from d6_paired_cold import percentiles
from d6_paired_replacement import TRANSPORT
from d6_paired_short_stream import MAXIMUM_SAMPLES, stream_plan
from d6_paired_steady import HOST_FIELDS, matrix

PHASE_SAMPLE = struct.Struct("<qqq")
PHASES = ("first_progress", "business", "owners")


def dynamic_stream_plan(transport, family, items, interceptor):
    cell = stream_plan(transport, 1, family, items)
    matrix.require(interceptor in ("none", "forward"), "unregistered dynamic interceptor")
    return dict(cell, family=family, interceptor=interceptor,
                operation=f"dynamic-{family}-{items}-{interceptor}",
                arguments={"duration": cell["arguments"]["duration"], "warmup": cell["arguments"]["warmup"]})


def read_dynamic_stream(path, arm, source, cell, hosts):
    report = matrix.read_json(path)
    matrix.require(report["Workload"] == "SharpLink.IntegrationTests.QualifiedDynamicShortStreams" and
                   report["SchemaVersion"] == 1 and report["SourceCommit"] == source and
                   report["Runtime"].startswith(".NET 10."), "dynamic stream report identity mismatch")
    duration = cell["arguments"]["duration"]
    for key, expected in {
        "Transport": TRANSPORT[cell["transport"]][0], "Family": cell["family"], "Items": cell["items"],
        "Interceptor": cell["interceptor"], "WarmupSeconds": cell["arguments"]["warmup"], "MeasureSeconds": duration,
        "SampleCapacity": MAXIMUM_SAMPLES,
    }.items():
        matrix.require(report[key] == expected, f"dynamic stream configuration differs: {key}")
    matrix.require(report["RpcTracingEnabled"] is False, "dynamic streams were recorded under RPC tracing")
    count = report["Completed"]
    matrix.require(type(count) is int and cell["minimum_completed"] <= count <= MAXIMUM_SAMPLES,
                   "dynamic stream completion count is outside the frozen population")
    expected_input_streams = 0 if cell["family"] == "s2c" else count
    matrix.require(report["ValidatedItems"] == count * cell["items"] and
                   report["ServerInputItems"] == expected_input_streams * cell["items"] and
                   report["ServerInputExits"] == expected_input_streams, "dynamic stream business counts differ")

    freq, elapsed = report["StopwatchFrequency"], report["ElapsedTicks"]
    cpu, alloc, gen0 = report["CpuTimeTicks"], report["ManagedAllocatedBytes"], report["Gen0"]
    matrix.require(type(freq) is int and freq > 0 and type(elapsed) is int and duration * freq <= elapsed < 130 * freq
                   and type(cpu) is int and cpu > 0 and type(alloc) is int and alloc >= 0 and type(gen0) is int and gen0 >= 0,
                   "dynamic stream trial duration or counters differ")
    identity = tuple(report[key] for key in HOST_FIELDS) + (freq,)
    matrix.require(all(value not in ("", None, "unknown") for value in identity), "dynamic stream host evidence missing")
    hosts.add(identity)
    matrix.require(len(hosts) == 1, "dynamic stream host or runtime changed")

    try:
        with gzip.open(path.with_suffix(".streams.bin.gz"), "rb") as data:
            raw = data.read(count * PHASE_SAMPLE.size + 1)
    except EOFError as error:
        raise ValueError("dynamic stream trace is incomplete") from error
    matrix.require(len(raw) == count * PHASE_SAMPLE.size, "dynamic stream trace population differs")
    phases = ([], [], [])
    for first, business, owners in PHASE_SAMPLE.iter_unpack(raw):
        matrix.require(0 < first <= business <= owners <= elapsed, "dynamic stream phase order is invalid")
        for population, ticks in zip(phases, (first, business, owners)):
            population.append(ticks)
    matrix.require(sum(phases[2]) <= elapsed, "serial stream samples overlap the trial duration")
    stats = {f"{phase}_{key}": value for phase, population in zip(PHASES, phases)
             for key, value in percentiles(population, 1_000_000 / freq).items()}
    return dict(**stats, file=path.name, arm=arm, completed=count, items=report["ValidatedItems"],
                measured_s=elapsed/freq, ops_per_s=count*freq/elapsed, p50_us=stats["owners_p50_us"],
                p99_us=stats["owners_p99_us"], p999_us=stats["owners_p999_us"], cpu_us_per_op=cpu/(10*count),
                managed_b_per_op=alloc/count, gen0=gen0)
