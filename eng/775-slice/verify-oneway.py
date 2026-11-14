"""Verify the OneWay grammar/refusal/physical ownership inventory fixed before the first hosted subject."""
import json
import sys
from pathlib import Path

root, subject = Path(sys.argv[1]), sys.argv[2]
expected = json.loads(Path(__file__).with_name("oneway-expected-cases.json").read_text(encoding="utf-8"))
for transport in ("tcp", "sharedmemory"):
    report = json.loads((root / f"oneway-{transport}.json").read_text(encoding="utf-8"))
    cases = report["cases"]
    assert report["kind"] == "functional-oneway-input" and report["transport"] == transport and report["source"] == subject
    assert len(cases) == len(set(cases)) == report["passed"] == len(expected)
    assert set(cases) == set(expected), (set(expected) - set(cases), set(cases) - set(expected))
    print(f"PASS oneway {transport}: {len(expected)} exact OneWay grammar/refusal/physical ownership cases; source {subject}")
