"""Validate the receipt inventory frozen before the first hosted receipt run."""
import json
import sys
from pathlib import Path

root = Path(sys.argv[1])
subject = sys.argv[2]
expected = json.loads(Path(__file__).with_name("receipt-expected-cases.json").read_text(encoding="utf-8"))
for transport in ("tcp", "sharedmemory"):
    report = json.loads((root / f"receipt-{transport}.json").read_text(encoding="utf-8"))
    cases = report["cases"]
    assert report["kind"] == "functional-receipt" and report["transport"] == transport
    assert report["source"] == subject
    assert len(cases) == len(set(cases)) == report["passed"] == len(expected)
    assert set(cases) == set(expected), (set(expected) - set(cases), set(cases) - set(expected))
    print(f"PASS receipt {transport}: {len(expected)} exact receipt cases; source {subject}")
