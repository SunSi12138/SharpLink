#!/usr/bin/env python3
"""Create a diagnostic-only old/fixed lease-capture arm in a disposable checkout.

Both arms execute the public production AcquireSendCreditAsync method and include
the identical outside-gate pause hook and identity-only diagnostic snapshot.
The fixed arm changes only when expectedLease is captured. Do not apply this
overlay to the production research checkout or use it for performance evidence.
"""

import argparse
import hashlib
import json
from pathlib import Path


BASELINE = "f8ba89121c87a6410a0139c44d7db30eab877f41"
REL = "src/SharpLink.Runtime/StreamFlowController.cs"
TEST_REL = "test/SharpLink.UnitTests/Runtime/StreamFlowControllerLeaseRaceTests.cs"
INPUTS = {
    "7694add9ffc1d858c80f0aa8f1f8815717068b8ff7181b86998672e68bfe60e2": "f8-current-dev",
    "a9cad11d9f80d8aedce4d412ada02ade32cd8048e0cfe133ad469d029eaf658d": "D-split-gates-only",
}

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


def sha(data):
    return hashlib.sha256(data).hexdigest()


def blob(data):
    return hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()


def transform(data, mode):
    digest = sha(data)
    if digest not in INPUTS:
        raise ValueError(f"Unsupported source SHA-256 {digest}; expected pinned f8 or exact D-only source")
    source = data.decode()
    start = source.index("    public ValueTask AcquireSendCreditAsync(")
    end = source.index("    [MethodImpl(MethodImplOptions.NoInlining)]", start)
    original = source[start:end]
    body = original
    tail = "        }\n\n        return AcquireContendedSendCreditAsync(\n"
    if body.count(tail) != 1 or body.count("            state?.Lease ?? 0L,") != 1:
        raise ValueError("AcquireSendCreditAsync control-flow anchor changed")
    if mode == "fixed":
        declaration = "        SendState? state;\n        lock (_gate)\n"
        if body.count(declaration) != 1:
            raise ValueError("Probe state declaration anchor changed")
        body = body.replace(declaration, "        SendState? state;\n        long expectedLease;\n        lock (_gate)\n")
        body = body.replace(tail,
            "            expectedLease = state?.Lease ?? 0L;\n        }\n\n" + HOOK +
            "        return AcquireContendedSendCreditAsync(\n")
        body = body.replace("            state?.Lease ?? 0L,", "            expectedLease,")
    elif mode == "original":
        body = body.replace(tail, "        }\n\n" + HOOK +
                            "        return AcquireContendedSendCreditAsync(\n")
        if body.replace(HOOK, "") != original:
            raise ValueError("Original arm changed production control flow beyond the pause hook")
    else:
        raise ValueError(mode)

    result = source[:start] + body + source[end:]
    marker = "    internal long SendConnectionCredit\n"
    if result.count(marker) != 1:
        raise ValueError("Diagnostic insertion anchor changed")
    result = result.replace(marker, DIAGNOSTICS + marker)
    # #840 arithmetic, waiter admission, pool behavior and all callers remain original.
    for begin, finish in [
        ("    public void ApplyWindowUpdate(", "    public void ReturnUnsentCredit("),
        ("    public void ReturnUnsentCredit(", "    public void CompleteSendStream("),
        ("    private SendState RentSendState(", "    private ReceiveState RentReceiveState("),
    ]:
        old = source[source.index(begin):source.index(finish)]
        new = result[result.index(begin):result.index(finish)]
        if old != new:
            raise ValueError(f"Unrelated production algorithm changed: {begin}")
    return result.encode()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--mode", required=True, choices=["original", "fixed"])
    parser.add_argument("--record", required=True, type=Path)
    args = parser.parse_args()
    source_path = args.root / REL
    data = source_path.read_bytes()
    result = transform(data, args.mode)
    test = Path(__file__).with_name("StreamFlowControllerLeaseRaceTests.cs").read_bytes()
    destination = args.root / TEST_REL
    if destination.exists():
        raise ValueError(f"Diagnostic test destination already exists: {destination}")
    source_path.write_bytes(result)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(test)
    record = {
        "schema": "sharplink-send-lease-capture-race-v1",
        "mode": args.mode,
        "auditedBaseline": BASELINE,
        "sourceFlavor": INPUTS[sha(data)],
        "sourcePath": REL,
        "inputSha256": sha(data), "inputGitBlob": blob(data),
        "outputSha256": sha(result), "outputGitBlob": blob(result),
        "testPath": TEST_REL, "testSha256": sha(test),
        "diagnosticHook": "same outside-gate hook in both arms",
        "publicEntryPoint": "AcquireSendCreditAsync",
        "reflectionInvocations": False,
        "flowCreditArithmeticChanged": False,
        "poolAlgorithmChanged": False,
        "productionPerformanceEvidence": False,
    }
    args.record.parent.mkdir(parents=True, exist_ok=True)
    args.record.write_text(json.dumps(record, indent=2) + "\n")
    print(json.dumps(record))


if __name__ == "__main__":
    main()
