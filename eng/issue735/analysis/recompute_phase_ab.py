#!/usr/bin/env python3
"""Recompute the pinned historical Phase A and Phase B model artifact evidence.

All input ZIPs/JSON stay unchanged. Different evidence families remain separate.
This script independently checks every paired microkernel row, real RPC record,
and c8 result, then executes the embedded frozen Phase B validation function
without depending on a local checkout or its summary-writing entry point.
"""
from collections import defaultdict
import argparse
import hashlib
import json
import math
from pathlib import Path
from statistics import median

BASE: Path
PHASE: Path
OUTPUT: Path
REVISIONS = {
    "baseline": "56c643cd308f294cb03df79d9f1214fb8292affb",
    "candidate": "3dab2b7599eda5ce9ead644352013e6d4a2aa0ae",
}


def read(path):
    return json.loads(path.read_text())


def micro(label, paths):
    cells = defaultdict(list)
    for path in paths:
        rows = read(path)
        assert len(rows) == 232
        seen = {}
        for row in rows:
            key = (row["Scenario"], row["ActiveStreams"], row["ItemsPerStream"])
            assert row["Mode"] in ("key", "resolved")
            assert row["Mode"] not in seen.setdefault(key, {})
            for field in ("NanosecondsPerItem", "AllocatedBytesPerItem", "LockContentionsPerItem"):
                assert math.isfinite(row[field]) and row[field] >= 0
            seen[key][row["Mode"]] = row
        assert len(seen) == 116
        for key, pair in seen.items():
            assert set(pair) == {"key", "resolved"}
            a, b = pair["key"], pair["resolved"]
            assert a["Checksum"] == b["Checksum"]
            cells[key].append({
                "file": str(path.relative_to(BASE)),
                "timeReductionPct": 100 * (1 - b["NanosecondsPerItem"] / a["NanosecondsPerItem"]),
                "allocDelta": b["AllocatedBytesPerItem"] - a["AllocatedBytesPerItem"],
                "contentionDelta": b["LockContentionsPerItem"] - a["LockContentionsPerItem"],
            })
    rows = [{
        "scenario": k[0], "streams": k[1], "items": k[2],
        "timeReductionPct": median(x["timeReductionPct"] for x in values),
        "allocationDelta": median(x["allocDelta"] for x in values),
        "contentionDelta": median(x["contentionDelta"] for x in values),
        "samples": values,
    } for k, values in cells.items()]
    scenarios = {
        scenario: median(row["timeReductionPct"] for row in rows if row["scenario"] == scenario)
        for scenario in sorted({row["scenario"] for row in rows})
    }
    long_lived = median(row["timeReductionPct"] for row in rows
        if row["items"] >= 1000 and row["scenario"] != "short-stream-control")
    if label != "nativeaot":
        old = read(PHASE / "micro-summary.json")[label[-1]]
        assert abs(long_lived - old["longLivedMedianGainPct"]) < 1e-9
        for key, value in scenarios.items():
            assert abs(value - old["scenarioMedianGainPct"][key]) < 1e-9
    else:
        old = read(PHASE / "aot-summary.json")
        for key, value in scenarios.items():
            assert abs(value - old[key]["gainPct"]) < 1e-9
    return {"rowCount": sum(len(read(path)) for path in paths),
        "longLivedTimeReductionPct": long_lived, "scenarios": scenarios, "cells": rows}


def generated_rpc():
    groups = defaultdict(lambda: defaultdict(list))
    for path in sorted((PHASE / "e2e").glob("*.json")):
        row = read(path)
        label = "baseline" if "-baseline-" in path.name else "candidate"
        assert row["commit"] == REVISIONS[label]
        assert row["validationFailures"] == 0 and row["operations"] > 0
        assert row["runtimeVersion"] == ".NET 10.0.12" and row["tieredPgo"] == "1"
        groups[(row["transport"], row["scenario"])][label].append(row)
    summary = read(PHASE / "e2e-summary.json")
    result = []
    for (transport, scenario), arms in sorted(groups.items()):
        assert set(arms) == {"baseline", "candidate"}
        assert all(len(values) == 3 for values in arms.values())
        a, b = arms["baseline"], arms["candidate"]
        pct = 100 * (median(r["throughputItemsPerSecond"] for r in b) /
                     median(r["throughputItemsPerSecond"] for r in a) - 1)
        assert abs(pct - summary[f"{transport}/{scenario}"]["itemsPct"]) < 1e-9
        result.append({"transport": transport, "scenario": scenario,
            "throughputDeltaPct": pct,
            "perRoundThroughputDeltaPct": [100 * (br["throughputItemsPerSecond"] /
                ar["throughputItemsPerSecond"] - 1) for ar, br in zip(a, b)]})
    return {"recordCount": sum(len(values) for arms in groups.values() for values in arms.values()),
        "cells": result}


def bounded_c8():
    result = []
    summary = read(PHASE / "c8-summary.json")
    for transport in ("tcp", "sharedmemory"):
        for size in (1, 10000):
            arms = {}
            for label in ("baseline", "candidate"):
                records = [read(path) for path in sorted((PHASE / "c8").glob(
                    f"{transport}-size{size}-{label}-r*.json"))]
                assert len(records) == 3
                for record in records:
                    assert record["SourceCommit"] == REVISIONS[label]
                    config = record["Configuration"]
                    assert config["StreamReceiveWindowBytes"] == 8192
                    assert config["ConnectionReceiveWindowBytes"] == 65536
                    assert config["ConcurrencyConfig"] == [8] and config["StreamSize"] == size
                    for row in record["Results"]:
                        assert row["Failure"] == row["ValidationFailure"] == row["Cancelled"] == 0
                        assert row["Success"] > 0
                arms[label] = records
            for operation in ("unary", "c2s", "s2c", "duplex"):
                a, b = ([next(row for row in record["Results"] if row["Operation"] == operation)["Qps"]
                    for record in arms[label]] for label in ("baseline", "candidate"))
                pct = 100 * (median(b) / median(a) - 1)
                if operation != "unary":
                    assert abs(pct - summary[f"{transport}/size{size}/{operation}"]) < 1e-9
                result.append({"transport": transport, "itemsPerStream": size, "operation": operation,
                    "throughputDeltaPct": pct,
                    "perRoundThroughputDeltaPct": [100 * (y / x - 1) for x, y in zip(a, b)],
                    "formalComparable": False, "recordingMode": "off"})
    return {"recordCount": 24, "operationResultCount": 96, "cells": result,
        "explicitWindowBytes": {"stream": 8192, "connection": 65536}}


# Frozen validation function, renamed only; substantive body unchanged.
# Source: https://github.com/SunSi12138/SharpLink/blob/7d01a3f6ef3e1efbbf03f9b4ca33fbedd4769977/eng/summarize-flow-state-phase-b.py
# Whole original source SHA256: 467e26b53176bc32b695547470ffa23ee173976d23a7c0769d945cbaf88b7e42
def validate_phase_b_rows(rows):
    if not rows:
        raise ValueError("Empty evidence")
    seen = set()
    for r in rows:
        key = tuple(r[k] for k in ("Family", "Variant", "Shape", "ActiveStreams", "ItemBytes", "Repetition"))
        if key in seen:
            raise ValueError(f"Duplicate row: {key}")
        seen.add(key)
        for field in ("NsPerItem", "AllocatedBytesPerItem", "ConnectionGateEntriesPerItem", "OwnerHandoffsPerItem"):
            if not math.isfinite(r[field]) or r[field] < 0:
                raise ValueError(f"Invalid {field}: {key}")
        n = r["ActiveStreams"] * r["ItemsPerStream"] * (2 if r["Shape"] == "duplex" else 1)
        if r["Checksum"] != n * r["ItemBytes"]:
            raise ValueError(f"Checksum mismatch: {key}")
        if r["Family"] == "full-controller" and r["ConnectionGateEntriesPerItem"] != 2:
            raise ValueError(f"B0 must not claim fewer acquisitions/item: {key}")
        if r["Family"] in ("send-owner-model", "send-publication-model"):
            if r["Family"] == "send-publication-model":
                if (r["Variant"] != "B2-grant-4096" or r["Shape"] not in ("legacy-commit", "writer-owned") or
                        r.get("ReusableCommandsAllocated") != 0 or r.get("QueueBackpressureWaits") != 0):
                    raise ValueError(f"Invalid publication boundary or hidden ownership costs: {key}")
            grant_items = {"B1-item-queue": 1, "B2-grant-256": max(1, 256 // r["ItemBytes"]),
                           "B2-grant-1024": max(1, 1024 // r["ItemBytes"]),
                           "B2-grant-4096": max(1, 4096 // r["ItemBytes"])}[r["Variant"]]
            # Warmup processes 64 items and leaves the remainder of its last grant.
            remaining_grant_items = (-64) % grant_items
            refills = max(0, math.ceil((r["ItemsPerStream"] - remaining_grant_items) / grant_items))
            expected = (refills + math.ceil(r["ItemsPerStream"] / 64)) / r["ItemsPerStream"]
            if abs(r["OwnerHandoffsPerItem"] - expected) > 1e-9:
                raise ValueError(f"Owner coordination count mismatch: {key}")
            for field in ("ReusableCommandsAllocated", "QueueBackpressureWaits"):
                value = r.get(field)
                if value is not None and (type(value) is not int or value < 0):
                    raise ValueError(f"Invalid command attribution: {key}/{field}")
            if r.get("ReusableCommandsAllocated") not in (None, 0):
                raise ValueError(f"Steady stream unexpectedly allocated a reusable command: {key}")
            if r["QueueOperationsPerItem"] != 2 * r["OwnerHandoffsPerItem"]:
                raise ValueError(f"Queue reads+writes attribution mismatch: {key}")
        if r["RuntimeAtomicRmwPerItem"] is not None:
            raise ValueError("Do not invent runtime/Channel internal atomic counts")
    return len(rows)


def phase_b():
    results = {}
    for path in sorted((BASE / "b2_model").glob("*.json")):
        rows = read(path)
        validate_phase_b_rows(rows)
        grouped = defaultdict(list)
        for row in rows:
            grouped[tuple(row[key] for key in (
                "Family", "Shape", "ActiveStreams", "ItemBytes", "Variant"))].append(row)
        cells = []
        for key, values in grouped.items():
            cell = dict(zip(("family", "shape", "streams", "itemBytes", "variant"), key))
            for field in ("NsPerItem", "AllocatedBytesPerItem", "ConnectionGateEntriesPerItem",
                "OwnerHandoffsPerItem", "QueueOperationsPerItem", "ExplicitSubmissionRmwPerItem",
                "GateWaitNsPerItem", "GateHoldNsPerItem"):
                cell[field] = median(row[field] for row in values)
            cell["samples"] = values
            cells.append(cell)
        results[path.name] = {"rowCount": len(rows), "cells": cells}
    return results


def main():
    global BASE, PHASE, OUTPUT
    if not __debug__:
        raise RuntimeError("Run without -O; assertions validate the original evidence")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-root", type=Path, required=True,
        help="Extracted archive's evidence/historical directory")
    parser.add_argument("--output-dir", type=Path, required=True,
        help="Separate writable directory for recomputed reports")
    args = parser.parse_args()
    BASE, OUTPUT = args.evidence_root.resolve(), args.output_dir.resolve()
    PHASE = BASE / "phase_a"
    if BASE == OUTPUT:
        raise ValueError("Keep raw evidence and new reports in separate directories")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    phase_a = {label: micro(label, paths) for label, paths in (
        ("pgo0", sorted((PHASE / "micro").glob("pgo0-r*.json"))),
        ("pgo1", sorted((PHASE / "micro").glob("pgo1-r*.json"))),
        ("nativeaot", [PHASE / "aot/micro.json"]))}
    phase_a["generatedRpc"] = generated_rpc()
    phase_a["c8"] = bounded_c8()
    model = phase_b()
    (OUTPUT / "phase-a-recomputed.json").write_text(json.dumps(phase_a, indent=2) + "\n")
    (OUTPUT / "phase-b-model-recomputed.json").write_text(json.dumps(model, indent=2) + "\n")
    (OUTPUT / "phase-ab-analysis-provenance.json").write_text(json.dumps({
        "scriptSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "evidenceRoot": str(BASE), "outputDirectory": str(OUTPUT),
        "inputFamilies": ["phase_a", "b2_model"],
        "scope": "recomputation of frozen historical artifacts; not a new RPC measurement"
    }, indent=2) + "\n")
    print(json.dumps({"phaseAMicroRows": sum(phase_a[x]["rowCount"] for x in ("pgo0", "pgo1", "nativeaot")),
        "phaseARpcRecords": phase_a["generatedRpc"]["recordCount"], "phaseAC8Records": 24,
        "phaseBModelRows": sum(value["rowCount"] for value in model.values()),
        "verifiedArchivedSummaryNumbers": True}))


if __name__ == "__main__":
    main()
