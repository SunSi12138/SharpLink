#!/usr/bin/env python3
"""Apply the small D/B0 control only to a disposable, pinned current-dev checkout.

This is an experiment generator, not the B2/B3 production implementation. It
preserves all send-credit arithmetic, including #840, byte for byte. It adds no
state table, grant, queue, completion, per-item allocation, or protocol option.
"""
import argparse
import hashlib
import json
from pathlib import Path

BASE = "f8ba89121c87a6410a0139c44d7db30eab877f41"
BLOB = "3290f2f48c86569d532939803528e2e5939225bd"
REL = "src/SharpLink.Runtime/StreamFlowController.cs"


def blob(data):
    return hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()


def transform(data):
    if blob(data) != BLOB:
        raise ValueError(f"Expected {BASE}:{REL} blob {BLOB}; got {blob(data)}")
    source = data.decode()
    start = source.index("    public void AcceptReceived(")
    end = source.index("    public void Complete(Exception exception)")
    receive = source[start:end]
    if receive.count("lock (_gate)") != 4:
        raise ValueError("Receive-domain entry inventory changed")
    result = source[:start] + receive.replace("lock (_gate)", "lock (_receiveGate)") + source[end:]
    field = "    private readonly Lock _gate = new();"
    if result.count(field) != 1:
        raise ValueError("Send gate inventory changed")
    result = result.replace(field, field + "\n"
        "    // Send and receive state are independent. Complete is the only operation\n"
        "    // taking both gates, always send first, before publishing terminal state.\n"
        "    private readonly Lock _receiveGate = new();")
    marker = "        CreditWaiter[] waiters;\n        lock (_gate)\n"
    if result.count(marker) != 1:
        raise ValueError("Complete ownership inventory changed")
    result = result.replace(marker, marker + "        lock (_receiveGate)\n")
    # The entire send domain, including effective WindowUpdate return, must be unchanged.
    before_send = source[source.index("    public bool TryAcquireSendCredit("):start]
    after_send = result[result.index("    public bool TryAcquireSendCredit("):result.index("    public void AcceptReceived(")]
    if before_send != after_send:
        raise ValueError("Send-credit implementation changed")
    return result.encode()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--record", required=True, type=Path)
    args = parser.parse_args()
    path = args.root / REL
    data = path.read_bytes()
    candidate = transform(data)
    path.write_bytes(candidate)
    args.record.parent.mkdir(parents=True, exist_ok=True)
    args.record.write_text(json.dumps({"baseline": BASE, "path": REL,
        "inputBlob": BLOB, "outputBlob": blob(candidate),
        "sha256": hashlib.sha256(candidate).hexdigest(),
        "variant": "D-split-direction-gates", "productionScheduler": "dev-SendPump",
        "sendCreditAlgorithmUnchanged": True}, indent=2) + "\n")


if __name__ == "__main__":
    main()
