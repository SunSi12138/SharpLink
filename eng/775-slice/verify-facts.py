"""Verify the facts/ABI inventory fixed before the first hosted subject."""
import json
import sys
from pathlib import Path

root, subject = Path(sys.argv[1]), sys.argv[2]
expected = json.loads(Path(__file__).with_name("facts-expected-cases.json").read_text(encoding="utf-8"))
for transport in ("tcp", "sharedmemory"):
    report = json.loads((root / f"facts-{transport}.json").read_text(encoding="utf-8"))
    cases = report["cases"]
    assert report["kind"] == "functional-method-facts" and report["transport"] == transport and report["source"] == subject
    assert len(cases) == len(set(cases)) == report["passed"] == len(expected)
    assert set(cases) == set(expected), (set(expected) - set(cases), set(cases) - set(expected))
    print(f"PASS facts {transport}: {len(expected)} exact facts/ABI cases; source {subject}")
