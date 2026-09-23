#!/usr/bin/env python3
"""Validate research rows and summarize within-family comparisons only."""
import argparse
import json
import math
import statistics
from pathlib import Path


def validate(rows):
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


def summarize(root):
    lines = ["# Phase B research — not a production acceptance report", "",
             "B0 vs A uses the full frozen controller. B2 vs B1 uses a send-only accounting model; do not compare those families as production equivalents.", "",
             "Gate timestamps are collected in separate instrumented runs. Non-instrumented gate counts use the audited two-entry source inventory.", "",
             "Owner handoffs count submitted commands, not OS context switches. Explicit RMW counts exclude Channel/Lock/runtime internals (unmeasured).", ""]
    paths = sorted(root.glob("*.json"))
    if not paths:
        raise ValueError("No raw reports")
    for path in paths:
        rows = json.loads(path.read_text())
        count = validate(rows)
        lines += [f"## {path.name}: {count} validated rows", "",
                  "| Family | Shape | Active streams | Item B | Variant | Median ns/item | B/item | Gates/item | Owner handoffs/item |", "|---|---|---:|---:|---|---:|---:|---:|---:|"]
        groups = {}
        for row in rows:
            key = tuple(row[k] for k in ("Family", "Shape", "ActiveStreams", "ItemBytes", "Variant"))
            groups.setdefault(key, []).append(row)
        for key, values in sorted(groups.items()):
            med = lambda field: statistics.median(r[field] for r in values)
            family, shape, streams, size, variant = key
            lines.append(f"| {family} | {shape} | {streams} | {size} | {variant} | {med('NsPerItem'):.2f} | {med('AllocatedBytesPerItem'):.3f} | {med('ConnectionGateEntriesPerItem'):.6f} | {med('OwnerHandoffsPerItem'):.6f} |")
        lines.append("")
    return "\n".join(lines)


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("root", type=Path)
    args = p.parse_args()
    result = summarize(args.root)
    (args.root / "summary.md").write_text(result + "\n")
    print(result)
