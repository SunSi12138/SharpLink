#!/usr/bin/env python3
"""Compare the exact split-publication reference with direct writer admission."""
import argparse
import importlib.util
import itertools
import json
import re
import statistics
from pathlib import Path

BASELINE = "141608ce1c256c0e92d0c5611491cb24af419cda"
RUNTIMES = ("pgo0", "pgo1", "aot")
SIZES = (1, 4096)
REPETITIONS = 4
SPEC = importlib.util.spec_from_file_location("phase_b_summary", Path(__file__).with_name("summarize-flow-state-phase-b.py"))
summary = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(summary)


def validate_report(rows, label, length):
    summary.validate(rows)
    expected = set(itertools.product((1, 8, 32, 128), (16, 4096), range(REPETITIONS)))
    seen, groups = set(), {}
    for row in rows:
        key = (row["ActiveStreams"], row["ItemBytes"], row["Repetition"])
        if (key not in expected or key in seen or row["Family"] != "send-fused-admission-model" or
                row["Variant"] != "B2-grant-4096" or row["ItemsPerStream"] != length or
                row["Workers"] != row["ActiveStreams"] or row["Instrumented"] or row["NsPerItem"] <= 0 or
                row["ConnectionGateEntriesPerItem"] != 0 or
                row["Shape"] != ("split-writer-admission" if label == "before" else "direct-writer-admission")):
            raise ValueError("Unexpected/duplicate fused-admission workload or missing writer ownership")
        seen.add(key)
        groups.setdefault(key[:2], []).append(row)
    if seen != expected:
        raise ValueError("Incomplete c1/c8/c32/c128 tiny/4KiB fused-admission matrix")
    return groups


def compare(root, runtimes=RUNTIMES):
    provenance = json.loads((root / "provenance.json").read_text())
    if (provenance.get("baseline") != BASELINE or
            re.fullmatch(r"[a-f0-9]{40}", provenance.get("candidate", "")) is None or
            provenance["candidate"] == BASELINE):
        raise ValueError("Missing exact writer-owned baseline/candidate revisions")
    cases = tuple(itertools.product(runtimes, SIZES, (1, 2), ("before", "after")))
    filenames = {f"{rt}-{n}-r{k}-{label}.json" for rt, n, k, label in cases}
    if {p.name for p in root.glob("*.json")} != filenames | {"provenance.json"}:
        raise ValueError("Require complete matched reports; reject missing or unrelated JSON")
    data = {}
    for runtime, length, launch, label in cases:
        name = f"{runtime}-{length}-r{launch}-{label}.json"
        rows = json.loads((root / name).read_text())
        for key, values in validate_report(rows, label, length).items():
            data.setdefault((runtime, length, label, *key), []).extend(values)
    lines = ["# Direct writer admission — model evidence, not full transport acceptance", "",
             f"Reference: `{BASELINE}`; candidate: `{provenance['candidate']}`.", "",
             f"{len(filenames)} reports / {len(filenames) * 32} rows; two process launches in AB/BA order, four samples per case/launch.", "",
             "Both revisions acquire AND settle explicit writer ownership. This is not a comparison against the legacy immediate Commit.", "",
             "64-item warmup per stream is outside timing. Length 1 is a warmed short-tail control, NOT a cold one-item stream. Setup, lifecycle and transport costs remain unmeasured.", "",
             "Positive time delta means slower. Medians are exploratory, not a stable production Go threshold. All regressions are retained.", "",
             "| Runtime | Items/stream | Streams | Item B | Split ns/item | Direct ns/item | Time delta | Split B/item | Direct B/item | Owner commands/item |",
             "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
    for runtime, length, streams, size in itertools.product(runtimes, SIZES, (1, 8, 32, 128), (16, 4096)):
        before, after = data[runtime, length, "before", streams, size], data[runtime, length, "after", streams, size]
        med = lambda rows, field: statistics.median(r[field] for r in rows)
        left, right = med(before, "NsPerItem"), med(after, "NsPerItem")
        if med(before, "OwnerHandoffsPerItem") != med(after, "OwnerHandoffsPerItem"):
            raise ValueError("Cannot attribute a changed owner coordination schedule to local gate elimination")
        lines.append(f"| {runtime} | {length} | {streams} | {size} | {left:.2f} | {right:.2f} | {(right / left - 1)*100:+.2f}% | {med(before, 'AllocatedBytesPerItem'):.5f} | {med(after, 'AllocatedBytesPerItem'):.5f} | {med(after, 'OwnerHandoffsPerItem'):.8f} |")
    return "\n".join(lines) + "\n"


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    text = compare(args.root)
    (args.root / "fused-comparison.md").write_text(text)
    print(text)
