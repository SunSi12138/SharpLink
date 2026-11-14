"""Check the frozen functional case inventory and exact hosted subject."""
import json
import sys
from pathlib import Path

root = Path(sys.argv[1])
subject = sys.argv[2]
expected = json.loads(Path(__file__).with_name("routing-expected-cases.json").read_text())
for transport in ("tcp", "sharedmemory"):
    report = json.loads((root / f"routing-{transport}.json").read_text())
    cases = report["cases"]
    assert report["source"] == subject, ("source mismatch", report)
    assert report["transport"] == transport and report["kind"] == "functional-bound-routing"
    assert len(cases) == len(set(cases)) == report["passed"] == len(expected)
    assert set(cases) == set(expected), (set(expected) - set(cases), set(cases) - set(expected))
    print(f"PASS {transport}: {len(expected)} exact functional cases; source {subject}")
