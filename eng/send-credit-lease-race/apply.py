#!/usr/bin/env python3
"""Instrument a disposable checkout without supplying a production fix.

Both arms receive the identical pause and read-only snapshot. Removing those
insertions must reproduce the complete input file byte-for-byte. In particular,
this tool never moves, adds or substitutes the expected-lease capture.
"""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess


BASELINE = "f8ba89121c87a6410a0139c44d7db30eab877f41"
BASELINE_SOURCE_SHA256 = "7694add9ffc1d858c80f0aa8f1f8815717068b8ff7181b86998672e68bfe60e2"
SOURCE = "src/SharpLink.Runtime/StreamFlowController.cs"
TEST = "test/SharpLink.UnitTests/Runtime/StreamFlowControllerLeaseRaceTests.cs"
HOOK = "        AfterProbeBeforeContendedForTests?.Invoke();\n"
DIAGNOSTICS = """    // Diagnostic overlay only: identical in original and fixed arms.
    internal Action? AfterProbeBeforeContendedForTests { get; set; }

    internal (object State, long Lease, long Credit) CaptureSendStateForLeaseRaceTests(
        long requestId, ushort streamId)
    {
        lock (_gate)
        {
            var state = _sendStates[new StreamKey(requestId, streamId)];
            return (state, state.Lease, state.Credit);
        }
    }

"""


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def git_blob(data):
    return hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()


def git(root, *args):
    return subprocess.check_output(["git", *args], cwd=root)


def instrument(data):
    source = data.decode("utf-8")
    for symbol in ("AfterProbeBeforeContendedForTests", "CaptureSendStateForLeaseRaceTests"):
        if symbol in source:
            raise ValueError("Source already contains the diagnostic symbol: " + symbol)
    begin = "    public ValueTask AcquireSendCreditAsync(\n"
    end = "    [MethodImpl(MethodImplOptions.NoInlining)]\n"
    if source.count(begin) != 1:
        raise ValueError("Public AcquireSendCreditAsync anchor is not unique")
    start = source.index(begin)
    finish = source.index(end, start)
    original = source[start:finish]
    tail = "        }\n\n        return AcquireContendedSendCreditAsync(\n"
    if original.count(tail) != 1:
        raise ValueError("Outside-gate contended-call boundary changed; review instrumentation")
    observed = original.replace(tail, "        }\n\n" + HOOK +
                                "        return AcquireContendedSendCreditAsync(\n", 1)
    result = source[:start] + observed + source[finish:]
    marker = "    internal long SendConnectionCredit\n"
    if result.count(marker) != 1:
        raise ValueError("Diagnostic snapshot insertion anchor is not unique")
    result = result.replace(marker, DIAGNOSTICS + marker, 1)
    restored = result.replace(HOOK, "", 1).replace(DIAGNOSTICS, "", 1).encode("utf-8")
    if restored != data:
        raise ValueError("Instrumentation changed source beyond the two declared insertions")
    return result.encode("utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True, help="Pristine disposable Git worktree")
    parser.add_argument("--arm", choices=("old", "current"), required=True)
    parser.add_argument("--subject-sha", required=True, help="Exact checkout commit, not a branch name")
    parser.add_argument("--record", type=Path, required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    head = git(root, "rev-parse", "HEAD").decode().strip()
    if head != args.subject_sha:
        raise ValueError("Checkout HEAD differs from the requested subject SHA")
    if git(root, "status", "--porcelain", "--untracked-files=all"):
        raise ValueError("Instrumentation requires a pristine disposable worktree")
    data = (root / SOURCE).read_bytes()
    if git(root, "show", "HEAD:" + SOURCE) != data:
        raise ValueError("Production input differs from the committed subject")
    if args.arm == "old" and (head != BASELINE or sha256(data) != BASELINE_SOURCE_SHA256):
        raise ValueError("Old control must use the exact audited f8 source")
    output = instrument(data)
    test = Path(__file__).with_name("StreamFlowControllerLeaseRaceTests.cs").read_bytes()
    destination = root / TEST
    if destination.exists():
        raise ValueError("Diagnostic test destination already exists")
    (root / SOURCE).write_bytes(output)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(test)
    record = {
        "schema": "sharplink-send-lease-regression-instrumentation-v1",
        "arm": args.arm, "subjectSha": head, "oldControlSha": BASELINE,
        "sourcePath": SOURCE, "inputSha256": sha256(data),
        "inputGitBlob": git_blob(data), "outputSha256": sha256(output),
        "outputGitBlob": git_blob(output), "testPath": TEST, "testSha256": sha256(test),
        "instrumenterSha256": sha256(Path(__file__).read_bytes()),
        "hookSha256": sha256(HOOK.encode()), "snapshotSha256": sha256(DIAGNOSTICS.encode()),
        "inputRecoveredExactlyAfterRemovingInstrumentation": True,
        "productionAlgorithmSuppliedByTool": False,
        "productionPerformanceEvidence": False,
    }
    args.record.parent.mkdir(parents=True, exist_ok=True)
    args.record.write_text(json.dumps(record, indent=2) + "\n")
    print(json.dumps(record))


if __name__ == "__main__":
    main()
