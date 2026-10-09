#!/usr/bin/env python3
"""Generate isolated A/B0 controllers; never edit the shipping runtime.

The input must be the reviewed Phase A snapshot. A new baseline requires an
explicit hash update and another review of the send/receive field ownership.
"""
import argparse
import hashlib
from pathlib import Path

PIN = "65892a1d9cef5a79b6afb865e667935a4b5b5e33"
# Git blob hashes include the header, so they also validate exact byte length.
EXPECTED = {
    "StreamFlowController.cs": "4354ef1793fc256d0d9bf729a957f1262098086d",
    "StreamFlowController.FirstAdmission.cs": "b0857e644a7d53ada534fc3464ac185d9724dad2",
}


def read_sources(root):
    sources = {}
    for name, expected in EXPECTED.items():
        data = (root / "src/SharpLink.Runtime" / name).read_bytes()
        digest = hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()
        if digest != expected:
            raise ValueError(f"{name}: expected Phase A {PIN} blob {expected}, got {digest}")
        sources[name] = data.decode("utf-8")
    return sources


def split_source(source):
    # Only the receive public/internal entry points take the receive gate.
    first = source.index("    internal ResolvedReceiveCreditLease ResolveReceiveCreditLease(")
    last = source.index("    public void Complete(Exception exception)")
    prefix, receive, suffix = source[:first], source[first:last], source[last:]
    if receive.count("lock (_gate)") != 8:
        raise ValueError("Receive entry-point inventory changed; re-audit ownership")
    source = prefix + receive.replace("lock (_gate)", "lock (_receiveGate)") + suffix
    source = source.replace("private readonly Lock _gate = new();",
                            "private readonly Lock _gate = new();\n    private readonly Lock _receiveGate = new();")
    # Terminal publication and both pools are atomic wrt either domain. No other
    # method nests gates; the sole lock order is send then receive.
    marker = "        CreditWaiter[] waiters;\n        lock (_gate)\n"
    if source.count(marker) != 1:
        raise ValueError("Terminal entry-point inventory changed")
    return source.replace(marker, marker + "        lock (_receiveGate)\n")


def generate(root, output):
    sources = read_sources(root)
    output.mkdir(parents=True, exist_ok=True)
    for variant in ("PhaseA", "SplitGate"):
        for name, original in sources.items():
            source = split_source(original) if variant == "SplitGate" and name == "StreamFlowController.cs" else original
            source = source.replace("namespace SharpLink.Runtime;", f"namespace SharpLink.FlowStatePhaseB.{variant};")
            source = source.replace("readonly Lock _", "readonly ProbeGate _")
            source = source.replace("lock (_gate)", "using (_gate.EnterScope())")
            source = source.replace("lock (_receiveGate)", "using (_receiveGate.EnterScope())")
            (output / f"{variant}.{name}").write_text(source)
        recv = "_receiveGate" if variant == "SplitGate" else "_gate"
        (output / f"{variant}.Metrics.cs").write_text(
            f"namespace SharpLink.FlowStatePhaseB.{variant};\n"
            "internal sealed partial class StreamFlowController\n{\n"
            "    internal ProbeGate SendGate => _gate;\n"
            f"    internal ProbeGate ReceiveGate => {recv};\n"
            "}\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--apply-b0-to-worktree", action="store_true")
    args = parser.parse_args()
    if args.apply_b0_to_worktree:
        sources = read_sources(args.root)
        path = args.root / "src/SharpLink.Runtime/StreamFlowController.cs"
        path.write_text(split_source(sources[path.name]))
    else:
        generate(args.root, args.output or args.root / "test/SharpLink.FlowStatePhaseB/obj/prototype")
