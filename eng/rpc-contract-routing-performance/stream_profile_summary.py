#!/usr/bin/env python3
"""Whole-capture stream CPU attribution; never a throughput acceptance metric."""
import argparse
from collections import defaultdict
import json
from pathlib import Path

PATTERNS = {
    "admissionSignal": ("RequestAdmissionWaiter.", "PendingCall.WaitForRequestAdmissionAsync(",
                        "PendingCall.TryAcceptRequest(", "PendingRequestTable.WaitForRequestAdmissionAsync("),
    "booleanTcsNotAdmissionSpecific": ("TaskCompletionSource`1[System.Boolean]",),
    "contextConstruction": ("SharpLinkServer.CreateCallContext(", "StreamCallContextSnapshot..ctor(",
                            "SharpLinkServer.CreateServerInvocationContext("),
    "taskScheduling": ("ThreadPoolWorkQueue.", "Task.RunContinuations(",
                       "AwaitTaskContinuation.", "ManualResetValueTaskSourceCore`1"),
}


def summarize(root):
    rows = []
    for slot in sorted((root / "stream-profiles").iterdir()):
        assert json.loads((slot / "command.json").read_text())["exitCode"] == 0
        files = sorted(slot.rglob("*.speedscope.json"))
        assert len(files) == len(list(slot.rglob("*.nettrace"))) == 4
        for path in files:
            doc = json.loads(path.read_text())
            names = [frame["name"] for frame in doc["shared"]["frames"]]
            totals = defaultdict(float)
            for thread in doc["profiles"]:
                assert thread["type"] == "evented" and thread["unit"] == "milliseconds"
                stack, previous = [], thread["startValue"]
                for event in thread["events"]:
                    elapsed = event["at"] - previous
                    assert elapsed >= 0
                    if stack and elapsed > 0:
                        active = [names[index] for index in stack]
                        # Includes warmup, runtime work and asynchronous continuations. It does
                        # not pretend a synchronous benchmark frame covers an async operation.
                        totals["wholeCaptureMilliseconds"] += elapsed
                        for category, patterns in PATTERNS.items():
                            if any(pattern in name for name in active for pattern in patterns):
                                totals[category] += elapsed
                    if event["type"] == "O":
                        stack.append(event["frame"])
                    else:
                        assert event["type"] == "C" and stack.pop() == event["frame"]
                    previous = event["at"]
                assert not stack
            assert totals["wholeCaptureMilliseconds"] > 0
            rows.append(dict(slot=slot.name, file=path.name, milliseconds=dict(totals),
                             percent={key: totals[key] * 100 / totals["wholeCaptureMilliseconds"]
                                      for key in PATTERNS}))
    assert len(rows) == 16
    report = dict(commits=json.loads((root / "provenance.json").read_text())["commits"],
                  patterns=PATTERNS, rows=rows,
                  limits="Whole capture including warmup and runtime threads; inclusive categories overlap. "
                         "Inlining/unresolved frames can hide helpers. Samples are not allocation stacks, "
                         "wait durations, per-request CPU costs or evidence of causal throughput improvement.")
    (root / "stream-profile-summary.json").write_text(json.dumps(report, indent=2) + "\n")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    print(json.dumps(summarize(parser.parse_args().root), indent=2))
