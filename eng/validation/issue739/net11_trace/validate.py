"""Independent diagnostic contract. Stable run_pilot.validate_sample stays strict."""
import math
from pathlib import Path
from project import pilot, require

OPERATIONS, WARMUP = 131072, 32768
CASES = ("tcp-add-c1", "shm-add-c1")
SEQUENCE = (("A", False), ("B", False), ("B", True), ("A", True), ("A", True),
            ("B", True), ("B", False), ("A", False), ("A", False), ("A", False))
TRACE_VERSION = "9.0.661903"
DECODER_PACKAGE = "3.1.30"
TOOL_SDK, TOOL_RUNTIME = "10.0.112", "10.0.12"


def validate_sample(row, case, sample, traced, driver_hash, corelib_hash):
    require(case in CASES, "Unsupported diagnostic case")
    require(type(row["schemaVersion"]) is int and row["schemaVersion"] == 1 and row["sourceSha"] == pilot.SOURCE and row["sample"] == sample, "Sample identity mismatch")
    require(row["runtime"] == pilot.ENVIRONMENT_VERSION and pilot.RUNTIME in row["framework"], "Sample runtime mismatch")
    require(Path(row["corelibPath"]).parent.name == pilot.RUNTIME and row["corelibSha256"] == corelib_hash, "Loaded target runtime/corelib mismatch")
    for name, value in (("operations", OPERATIONS), ("warmup", WARMUP), ("checks", OPERATIONS),
                        ("received", OPERATIONS + 1), ("expectedReceived", OPERATIONS + 1), ("concurrency", 1), ("connectionCount", 1),
                        ("threadPoolMinimumWorkers", 132), ("threadPoolMinimumIo", 132)):
        require(type(row[name]) is int and row[name] == value, "Exact shape/counter mismatch: " + name)
    require(row["transport"] == case.split("-")[0] and row["kind"] == "add", "Workload identity mismatch")
    require(type(row["processId"]) is int and row["processId"] > 0, "Invalid process ID")
    require(row["precise"] is True and row["driverIncluded"] is True and row["subtractionApplied"] is False, "Measurement contract mismatch")
    require(row["diagnostic"] is True and row["net11TraceDiagnostic"] is True and row["traceEnabled"] is traced, "Diagnostic/trace mode mismatch")
    require(row["markerWarmupCompleted"] is True and row["markerEnabledAtStart"] is traced and row["markerEnabledAtStop"] is traced, "Marker provider state mismatch")
    require(row["markerProvider"] == "SharpLink-Issue739" and "wider" in row["markerBoundary"], "Marker contract mismatch")
    require(row["executableSha256"] == driver_hash, "Fixed consumer bytes changed")
    require(row["architecture"] == "X64" and row["serverGc"] is False, "Architecture/GC mismatch")
    for key in ("bytes", "gen0", "ticksStart", "ticksEnd", "stopwatchFrequency", "markerStartTicks", "markerStopTicks"):
        require(type(row[key]) is int and row[key] >= 0, "Invalid integer: " + key)
    require(row["markerStartTicks"] < row["ticksStart"] < row["ticksEnd"] < row["markerStopTicks"] and row["stopwatchFrequency"] > 0, "Marker/measurement window mismatch")
    for key in ("bytesPerOperation", "elapsedSeconds", "qps", "cpuMilliseconds", "cpuNanosecondsPerOperation", "p50Nanoseconds", "p99Nanoseconds"):
        require(type(row[key]) in (int, float) and math.isfinite(row[key]) and row[key] >= 0, "Invalid metric: " + key)
    require(row["bytesPerOperation"] == row["bytes"] / OPERATIONS, "Allocation denominator mismatch")
    require(row["elapsedSeconds"] > 0 and row["qps"] > 0, "Empty timing interval")
    require(row["p99Nanoseconds"] >= row["p50Nanoseconds"], "Latency quantiles reversed")
    require(math.isclose(row["cpuNanosecondsPerOperation"], row["cpuMilliseconds"] * 1e6 / OPERATIONS, rel_tol=1e-12), "CPU denominator mismatch")
    duration = (row["ticksEnd"] - row["ticksStart"]) / row["stopwatchFrequency"]
    require(math.isclose(row["elapsedSeconds"], duration, rel_tol=1e-12) and math.isclose(row["qps"], OPERATIONS / duration, rel_tol=1e-12), "Timing denominator mismatch")


def validate_trace(meta, sample, ticks, capture, trace_hash, decoder_corelib_hash):
    require(meta["schemaVersion"] == 1 and meta["conversionCompleted"] is True, "Decoder conversion incomplete")
    require(meta["sample"] == sample["sample"] and meta["pid"] == sample["processId"], "Trace marker identity mismatch")
    require(meta["markerBytes"] == sample["bytes"] and meta["operations"] == sample["operations"], "Trace marker byte/operation mismatch")
    require(meta["markerProvider"] == "SharpLink-Issue739" and meta["markerStopMilliseconds"] > meta["markerStartMilliseconds"], "Invalid marker window")
    require(meta["decoderVersion"] == DECODER_PACKAGE + ".0", "Actual TraceEvent assembly version mismatch")
    require(meta["decoderRuntime"] == TOOL_RUNTIME and Path(meta["decoderCorelibPath"]).parent.name == TOOL_RUNTIME and meta["decoderCorelibSha256"] == decoder_corelib_hash, "Decoder loaded runtime mismatch")
    require(meta["traceSha256"] == capture["traceSha256"] == trace_hash, "Trace content hash mismatch")
    require(capture["schemaVersion"] == 1 and capture["exitCode"] == 0 and capture["abortReason"] is None, "Collector failed")
    require(capture["toolVersion"].split("+", 1)[0] == TRACE_VERSION, "Collector version mismatch")
    require(capture["providerConfiguration"] == {"profile": "gc-verbose", "additionalProviders": "SharpLink-Issue739:0xffffffffffffffff:5", "bufferMiB": 256}, "Trace provider configuration mismatch")
    require(type(meta["eventsLostReported"]) is int and meta["eventsLostReported"] >= 0 and isinstance(meta["lossNotifications"], list), "Loss accounting unavailable or malformed")
    require(meta["unrecognizedAllocationEvents"] == 0, "Unknown allocation-event schema")
    require(len(ticks) == meta["tickEvents"] and len(ticks) > 0, "No complete allocation-tick stream")
    resolved = missing_stacks = missing_sizes = 0
    versions = set()
    for tick in ticks:
        require(tick["schemaVersion"] == 1 and tick["sample"] == sample["sample"] and tick["pid"] == sample["processId"], "Tick identity mismatch")
        require(type(tick["eventVersion"]) is int and tick["eventVersion"] in (2, 3, 4), "Unsupported allocation-tick version")
        require(type(tick["allocationAmount64"]) is int and tick["allocationAmount64"] > 0, "Missing/invalid allocation weight")
        require(isinstance(tick["typeName"], str) and bool(tick["typeName"].strip()), "Missing allocation type; net11 decoder compatibility unproven")
        require(isinstance(tick["frames"], list) and all(isinstance(frame, str) for frame in tick["frames"]), "Malformed allocation stack")
        require(type(tick["unresolvedFrames"]) is int and 0 <= tick["unresolvedFrames"] <= len(tick["frames"]), "Malformed unresolved-frame count")
        require(meta["markerStartMilliseconds"] < tick["timeRelativeMilliseconds"] < meta["markerStopMilliseconds"], "Tick outside marker window")
        resolved += any(frame and not frame.startswith("[unresolved:") for frame in tick["frames"])
        missing_stacks += not tick["frames"]
        missing_sizes += tick["eventVersion"] < 4 or type(tick.get("objectSize")) is not int or tick["objectSize"] <= 0
        versions.add(tick["eventVersion"])
    require(sorted(versions) == meta["allocationTickVersions"], "Event version aggregate mismatch")
    require(resolved > 0, "No resolved allocation stack; compatibility unproven")
    return {"schemaVersion": 1, "status": "compatible-for-bounded-diagnostics", "sample": sample["sample"],
            "traceSha256": trace_hash, "recognizedVersions": sorted(versions), "checkedTickCount": len(ticks),
            "ticksWithResolvedFrames": resolved, "ticksWithoutStacks": missing_stacks, "ticksWithoutObjectSize": missing_sizes,
            "eventsLostReported": meta["eventsLostReported"], "lossNotifications": meta["lossNotifications"],
            "meaning": "field/schema compatibility only; reported loss and missing stacks remain; no completeness/owner-coverage guarantee"}
