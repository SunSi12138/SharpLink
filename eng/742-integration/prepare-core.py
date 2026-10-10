#!/usr/bin/env python3
"""Materialize the core candidate in an isolated checkout; never push or change gates."""
from pathlib import Path
import subprocess
import hashlib
import json

ROOT = Path(__file__).resolve().parents[2]
EXPECTED = {
    "StreamManager.Routing.cs": "a8fa8e890ceaf341a552651fc289c3bddc0b1c4a",
    "StreamFlowController.cs": "4354ef1793fc256d0d9bf729a957f1262098086d",
}

def once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError(f"Expected one source anchor, found {text.count(old)}: {old[:100]}")
    return text.replace(old, new, 1)

def main():
    before = {}
    for name, expected in EXPECTED.items():
        path = ROOT / "src/SharpLink.Runtime" / name
        data = path.read_bytes()
        actual = hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()
        if actual != expected:
            raise RuntimeError(f"Source drift: {name}: {actual} != {expected}")
        before[name] = data.decode()

    name = "StreamManager.Routing.cs"
    text = before[name]
    prefix, suffix = text.split("    private sealed class DispatcherEntry : IStreamDispatchState", 1)
    if prefix.count(".TryClaimRetirement()") != 5:
        raise RuntimeError("Unexpected retirement call population")
    prefix = prefix.replace(".TryClaimRetirement()", ".TryClaimRetirementAndClose()")
    for line in ["                found.Close();\n", "                    defaultDispatcher.Close();\n",
                 "                        defaultDispatcher.Close();\n", "                        pair.Value.Close();\n",
                 "                        entry.Close();\n"]:
        prefix = once(prefix, line, "")
    old = "        internal bool TryClaimRetirement()\n            => (Interlocked.Or(ref _state, RetirementClaimedMask) & RetirementClaimedMask) == 0;"
    suffix = once(suffix, old, old + "\n\n        // Claiming retirement and excluding new acquisitions share one linearization point.\n        // Existing DATA counts, cleanup pins and detach ownership are not changed.\n        internal bool TryClaimRetirementAndClose()\n            => (Interlocked.Or(ref _state, RetirementClaimedMask | ClosedMask)\n                & RetirementClaimedMask) == 0;")
    (ROOT / "src/SharpLink.Runtime" / name).write_text(prefix + "    private sealed class DispatcherEntry : IStreamDispatchState" + suffix)

    name = "StreamFlowController.cs"
    text = before[name]
    anchor = "            // A benign double return can overshoot a window: the peer may"
    replacement = """            // AbortSendStreams returns all of this stream's remaining connection debt
            // locally before poisoning it. Once a poisoned stream is fully credited,
            // a delayed peer update cannot restore that connection credit a second time.
            // Keep active-stream advertised-permission clamps unchanged; failed completion
            // with outstanding debt still accepts its legitimate final update.
            if (state.AbortException is not null && state.Credit == _streamWindow)
                return;

""" + anchor
    text = once(text, anchor, replacement)
    (ROOT / "src/SharpLink.Runtime" / name).write_text(text)

    output = ROOT / "artifacts/742-integration"
    output.mkdir(parents=True, exist_ok=True)
    manifest = {
        "base": "e834d3c28c87ad496989af925515cf21babd308d",
        "mode": "core-only; B3 not enabled",
        "original_blobs": EXPECTED,
        "candidate_sha256": {name: hashlib.sha256((ROOT / "src/SharpLink.Runtime" / name).read_bytes()).hexdigest() for name in EXPECTED},
    }
    (output / "core-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    patch = subprocess.check_output(["git", "diff", "--binary", "--", "src/SharpLink.Runtime"], cwd=ROOT)
    (output / "core.patch").write_bytes(patch)
    print(json.dumps(manifest, indent=2))

if __name__ == "__main__":
    main()
