#!/usr/bin/env python3
"""Validate explicit writer ownership cost without claiming production/wire parity."""
import argparse
import importlib.util
import itertools
import json
import statistics
from pathlib import Path

spec = importlib.util.spec_from_file_location("summary", Path(__file__).with_name("summarize-flow-state-phase-b.py"))
summary = importlib.util.module_from_spec(spec)
spec.loader.exec_module(summary)


def compare(root):
    expected_files = {f"publication-{runtime}.json" for runtime in ("pgo0", "pgo1", "nativeaot")}
    if {p.name for p in root.glob("publication-*.json")} != expected_files:
        raise ValueError("Require all three exact-head JIT/AOT publication reports")
    lines = ["# Writer publication ownership — send model, not transport integration", "",
             "Both shapes use this same candidate and peer-update schedule. The legacy-commit control skips writer lifetime; writer-owned explicitly begins/settles it.", "",
             "Two samples per shape in AB/BA order. Positive time change means slower. Counts include refill and WindowUpdate; task/harness allocations exclude stream setup and cold reconciliation.", "",
             "| Runtime | Streams | Item B | Legacy ns/item | Writer-owned ns/item | Time change | Legacy B/item | Writer-owned B/item | Owner commands/item |",
             "|---|---:|---:|---:|---:|---:|---:|---:|---:|"]
    expected = set(itertools.product((1, 8, 32, 128), (16, 4096), ("legacy-commit", "writer-owned"), (0, 1)))
    for runtime in ("pgo0", "pgo1", "nativeaot"):
        rows = json.loads((root / f"publication-{runtime}.json").read_text())
        summary.validate(rows)
        seen = set()
        groups = {}
        for row in rows:
            key = (row["ActiveStreams"], row["ItemBytes"], row["Shape"], row["Repetition"])
            if (key not in expected or key in seen or row["ItemsPerStream"] != 4096 or
                    row["Family"] != "send-publication-model" or row["Workers"] != row["ActiveStreams"]):
                raise ValueError("Wrong or duplicate publication workload")
            seen.add(key)
            groups.setdefault(key[:3], []).append(row)
        if seen != expected:
            raise ValueError("Incomplete publication matrix")
        for streams, size in itertools.product((1, 8, 32, 128), (16, 4096)):
            old, new = groups[streams, size, "legacy-commit"], groups[streams, size, "writer-owned"]
            med = lambda rows, field: statistics.median(r[field] for r in rows)
            before, after = med(old, "NsPerItem"), med(new, "NsPerItem")
            if before <= 0 or med(old, "OwnerHandoffsPerItem") != med(new, "OwnerHandoffsPerItem"):
                raise ValueError("Invalid timing or changed connection-owner coordination frequency")
            lines.append(f"| {runtime} | {streams} | {size} | {before:.2f} | {after:.2f} | {(after/before-1)*100:+.2f}% | {med(old, 'AllocatedBytesPerItem'):.4f} | {med(new, 'AllocatedBytesPerItem'):.4f} | {med(new, 'OwnerHandoffsPerItem'):.8f} |")
    return "\n".join(lines) + "\n"


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    root = parser.parse_args().root
    text = compare(root)
    (root / "publication-comparison.md").write_text(text)
    print(text)
