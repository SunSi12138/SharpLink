"""Verify the exact lifetime inventory and child provenance from a fresh TUnit run."""

import argparse
import json
from pathlib import Path


def strings(node):
    if isinstance(node, str):
        yield node
    elif isinstance(node, dict):
        for value in node.values():
            yield from strings(value)
    elif isinstance(node, list):
        for value in node:
            yield from strings(value)


parser = argparse.ArgumentParser()
parser.add_argument("--results", type=Path, required=True)
parser.add_argument("--architecture", choices=["X64", "Arm64"], required=True)
args = parser.parse_args()
if not __debug__:
    raise RuntimeError("Run this validation without Python -O or PYTHONOPTIMIZE")
reports = list(args.results.glob("*.tunit-report.json"))
assert len(reports) == 1, "Expected exactly one fresh lifetime report"
report = json.loads(reports[0].read_text(encoding="utf-8"))
summary = report["summary"]
assert summary["total"] == 37 and summary["passed"] == 37
for key in ("failed", "skipped", "cancelled", "timedOut"):
    assert summary[key] == 0, (key, summary)
records = {}
prefix = "READ_OWNERSHIP_ALLOCATION_VERIFIED|"
for value in strings(report):
    for line in value.splitlines():
        if line.startswith(prefix):
            record = json.loads(line[len(prefix):])
            prior = records.setdefault(record["nonce"], record)
            assert prior == record, "Conflicting child provenance for the same nonce"
assert len(records) == 2, "Expected both distinct once-only child measurements"
assert {r["scenario"] for r in records.values()} == {
    "SuccessfulSuspensionsShouldNotAllocateAfterWarmup",
    "RegisteredConsumerSuspensionsShouldNotAllocateAfterWarmup",
}
assert len({r["assemblyMvid"] for r in records.values()}) == 1
assert len({r["runtime"] for r in records.values()}) == 1
for record in records.values():
    assert record["architecture"] == args.architecture
    assert record["executed"] == 1 and record["warmup"] == 1000 and record["measured"] == 10000
    assert record["allocated"] == 0 and record["passed"] and record["cacheUnchanged"]
    assert record["cacheEntries"] == 4096
    assert record["nonDefaultContext"] and record["contextFlowEnabled"]
    print("VERIFIED_LIFETIME_ORACLE:", json.dumps(record, separators=(",", ":")))
print("VERIFIED_LIFETIME_INVENTORY: 37 passed, 0 failed/skipped/cancelled/timed-out")
