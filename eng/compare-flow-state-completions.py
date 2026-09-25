#!/usr/bin/env python3
"""Same-harness, exact-revision comparison; allocation elimination is not production Go."""
import argparse
import importlib.util
import itertools
import json
import math
import statistics
from pathlib import Path

BASELINE = "4d7335c8c542136d895d60339131164b0052426b"
VARIANTS = ("B1-item-queue", "B2-grant-256", "B2-grant-1024", "B2-grant-4096")
spec = importlib.util.spec_from_file_location("phase_b_summary", Path(__file__).with_name("summarize-flow-state-phase-b.py"))
summary = importlib.util.module_from_spec(spec)
spec.loader.exec_module(summary)


def compare(root):
    provenance = json.loads((root / "provenance.json").read_text())
    if provenance["baseline"] != BASELINE or provenance["candidate"] == BASELINE:
        raise ValueError("Comparison must distinguish exact reference and candidate")
    candidate = provenance["candidate"]
    if len(candidate) != 40 or any(c not in "0123456789abcdef" for c in candidate):
        raise ValueError("Candidate must be a complete Git commit SHA")
    groups = {}
    expected_files = set()
    for runtime, size, launch, label in itertools.product(("pgo0", "pgo1", "aot"), (1024, 16384), (1, 2), ("before", "after")):
        path = root / f"{runtime}-{size}-r{launch}-{label}.json"
        expected_files.add(path.name)
        rows = json.loads(path.read_text())
        summary.validate(rows)
        expected_rows = set(itertools.product(VARIANTS, (0, 1)))
        seen = set()
        for row in rows:
            key = (row["Variant"], row["Repetition"])
            if key not in expected_rows or key in seen:
                raise ValueError(f"Missing/duplicate/unknown case in {path}")
            seen.add(key)
            if (row["ActiveStreams"], row["ItemBytes"], row["ItemsPerStream"], row["Family"], row["Shape"]) != (128, 16, size, "send-owner-model", "periodic-update-64"):
                raise ValueError(f"Workload mismatch in {path}")
            if label == "after" and (row.get("ReusableCommandsAllocated") != 0 or row.get("QueueBackpressureWaits") != 0):
                raise ValueError(f"Completion reuse/backpressure boundary not met in {path}")
            if label == "before" and row.get("ReusableCommandsAllocated") is not None:
                raise ValueError(f"Reference cannot claim measurements absent from its code: {path}")
            groups.setdefault((runtime, size, row["Variant"], label), []).append(row)
        if seen != expected_rows:
            raise ValueError(f"Incomplete comparison: {path}")
    actual_files = {p.name for p in root.glob("*.json")} - {"provenance.json"}
    if actual_files != expected_files:
        raise ValueError("Unexpected comparison reports; do not silently mix runs")
    lines = ["# Reusable completion control — send model only", "",
             f"Reference `{BASELINE}`; candidate `{candidate}`.", "",
             "Both use the same Program/GrantProbe harness. Only candidate command metrics are enabled by a compile symbol; no flow algorithm is changed in the reference.", "",
             "Two launches in AB/BA order, two samples/variant/launch. Total allocation includes fixed producer Tasks/closures. It is not retained memory and excludes connection/stream setup and lifecycle commands.", "",
             "Positive ns change means SLOWER; do not interpret allocation reduction as a throughput win.", "",
             "| Runtime | Items/stream | Variant | Before ns/item | After ns/item | Time change | Before B/item | After B/item | After total allocated B |", "|---|---:|---|---:|---:|---:|---:|---:|---:|"]
    for runtime, size, variant in itertools.product(("pgo0", "pgo1", "aot"), (1024, 16384), VARIANTS):
        old = groups[runtime, size, variant, "before"]
        new = groups[runtime, size, variant, "after"]
        med = lambda rows, field: statistics.median(r[field] for r in rows)
        b, a = med(old, "NsPerItem"), med(new, "NsPerItem")
        allocation = med(new, "AllocatedBytesPerItem")
        if b <= 0 or not math.isfinite(a / b):
            raise ValueError("Invalid timing")
        lines.append(f"| {runtime} | {size} | {variant} | {b:.2f} | {a:.2f} | {(a/b-1)*100:+.2f}% | {med(old, 'AllocatedBytesPerItem'):.4f} | {allocation:.4f} | {allocation*128*size:.0f} |")
    return "\n".join(lines) + "\n"


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("root", type=Path)
    args = p.parse_args()
    text = compare(args.root)
    (args.root / "comparison.md").write_text(text)
    print(text)
