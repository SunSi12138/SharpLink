#!/usr/bin/env python3
"""Apply deterministic #803 attribution controls to a pristine historical checkout.

This is an isolated red/green experiment. It does not revive #803, modify dev,
relax an existing assertion, or certify either green overlay for production.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess

SUBJECT = "8ac4c84938d193c9a9659e9facd71f1fff82968a"
HASHES = {
    "test/SharpLink.UnitTests/Runtime/WriterOwnedSessionIntegrationTests.cs": "1c09522d3c8687988f52f68f3fbbeda348fc5bd0b41a7b248bffd3ff1cd5c1a6",
    "test/SharpLink.UnitTests/Client/WriterOwnedClientConnectionTests.cs": "e469f8e2da4b720c36754036fc3c694abbf730c2bb84e08f10cf3a8b0051acb3",
    "src/SharpLink.Runtime/WriterOwnedStreamScheduler.cs": "d9c20a693ab2be881df7209100728236c3e13885fb64b5340bee88cdf5fee008",
    "src/SharpLink.Client/ClientConnection.cs": "8e07144c0b842ee90b220b04a7ca48b87a8d060479dfb63193abb8c701cf4ea8",
    "src/SharpLink.Runtime/RpcSession.WriterOwnedCancellation.cs": "f7678b3ba0060c2f78a8891a86bf2a72a06e35bb0891e760a46b6806675eaf3e",
    "src/SharpLink.Runtime/RpcSession.WriterOwnedStreaming.cs": "b21391c93a7aca9c52467ddc797260de5c80c130796a72b4f446c158fd944b83",
}


def once(text: str, old: str, new: str) -> str:
    if text.count(old) != 1:
        raise ValueError("Missing/nonunique exact anchor: " + old[:100])
    return text.replace(old, new, 1)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkout", type=Path, required=True)
    parser.add_argument("--mode", choices=("red", "green"), required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()
    root = args.checkout.resolve()
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    if head != SUBJECT:
        raise ValueError(f"Historical subject mismatch: {head} != {SUBJECT}")
    original = {}
    for path, expected in HASHES.items():
        data = (root / path).read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if digest != expected:
            raise ValueError(f"Historical source drift at {path}: {digest} != {expected}")
        original[path] = data.decode("utf-8")
    transformed = dict(original)
    templates = Path(__file__).resolve().parent
    session_path = "test/SharpLink.UnitTests/Runtime/WriterOwnedSessionIntegrationTests.cs"
    client_test_path = "test/SharpLink.UnitTests/Client/WriterOwnedClientConnectionTests.cs"
    session_anchor = "    [Test]\n    public async Task IndependentStreamsMustShareOneConnectionWindow()"
    client_anchor = "    [Test]\n    public async Task ActualClientBusinessFailureMustFollowItsAcceptedData()"
    transformed[session_path] = once(original[session_path], session_anchor,
        (templates / "session-tests.cs.in").read_text() + "\n" + session_anchor)
    transformed[client_test_path] = once(original[client_test_path], client_anchor,
        (templates / "client-tests.cs.in").read_text() + "\n" + client_anchor)

    if args.mode == "green":
        path = "src/SharpLink.Runtime/WriterOwnedStreamScheduler.cs"
        anchor = "    internal StreamHandle Open(long requestId, ushort streamId)"
        helper = """    // Attribution overlay: terminal delivery belongs to an existing lifecycle.
    // Keep Open's rejection contract for callers trying to create/reopen a stream.
    // This immutable handle preserves a selected failure even if retirement follows.
    internal StreamHandle? FindExistingTerminalStream(long requestId, ushort streamId)
    {
        lock (_mapGate)
        {
            ThrowIfClosing();
            return _streams.GetValueOrDefault(new StreamKey(requestId, streamId));
        }
    }

"""
        transformed[path] = once(original[path], anchor, helper + anchor)
        path = "src/SharpLink.Runtime/RpcSession.WriterOwnedStreaming.cs"
        old = "        var stream = await ResolveWriterOwnedStreamAsync(requestId, streamId, default, cancellationToken).ConfigureAwait(false);"
        new = """        cancellationToken.ThrowIfCancellationRequested();
        var stream = GetOrCreateWriterOwnedStreams().FindExistingTerminalStream(requestId, streamId)
            ?? await ResolveWriterOwnedStreamAsync(requestId, streamId, default, cancellationToken).ConfigureAwait(false);"""
        transformed[path] = once(original[path], old, new)
        path = "src/SharpLink.Runtime/RpcSession.WriterOwnedCancellation.cs"
        transformed[path] = once(original[path],
            "    private static void AbortWriterOwnedLease(",
            "    internal static void AbortWriterOwnedLease(")
        path = "src/SharpLink.Client/ClientConnection.cs"
        text = original[path]
        text = once(text,
            "        var writerCancellation = default(CancellationTokenRegistration);",
            "        var sendCreditLease = default(StreamFlowController.ResolvedSendCreditLease);\n"
            "        var writerCancellation = default(CancellationTokenRegistration);")
        text = once(text,
            "            var sendCreditLease = default(StreamFlowController.ResolvedSendCreditLease);\n",
            "")
        text = once(text,
            """        finally
        {
            writerCancellation.Dispose();
        }
    }
""",
            """        finally
        {
            writerCancellation.Dispose();
            // Attribution overlay: callback delivery is not the producer's settlement barrier.
            // The immutable lease still owns its unadmitted preparation on cancellation.
            if (Session.UsesWriterOwnedStreams && cancellationToken.IsCancellationRequested)
                RpcSession.AbortWriterOwnedLease(sendCreditLease, new OperationCanceledException(cancellationToken));
        }
    }
""")
        transformed[path] = text

    rows = []
    for path, text in transformed.items():
        changed = text != original[path]
        if changed:
            (root / path).write_text(text)
        rows.append({"path": path, "originalSha256": HASHES[path],
            "overlaidSha256": hashlib.sha256(text.encode()).hexdigest(), "changed": changed})
    args.manifest.parent.mkdir(parents=True, exist_ok=True)
    args.manifest.write_text(json.dumps({"subjectSha": SUBJECT, "mode": args.mode, "overlayRevision": 2,
        "scope": "isolated-historical-root-cause-attribution; NOT a production candidate",
        "files": rows}, indent=2) + "\n")
    print(f"Applied {args.mode}: {sum(row['changed'] for row in rows)} files; exact subject {SUBJECT}")


if __name__ == "__main__":
    main()
