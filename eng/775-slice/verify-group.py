"""Validate the independently frozen group-lifecycle report inventory and subject."""
import json
import sys
from pathlib import Path

root = Path(sys.argv[1])
subject = sys.argv[2]
expected = json.loads(Path(__file__).with_name("group-expected-cases.json").read_text())
for transport in ("tcp", "sharedmemory"):
    report = json.loads((root / f"group-{transport}.json").read_text())
    cases = report["cases"]
    assert report["kind"] == "functional-group-lifecycle" and report["transport"] == transport
    assert report["source"] == subject
    assert len(cases) == len(set(cases)) == report["passed"] == len(expected)
    assert set(cases) == set(expected), (set(expected) - set(cases), set(cases) - set(expected))
    print(f"PASS group {transport}: {len(expected)} exact lifecycle cases; source {subject}")

