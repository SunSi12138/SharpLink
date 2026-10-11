#!/usr/bin/env python3
"""Issue #801: temporary 3.0 RPC channel/sink interface-shape experiment.

Run in GitHub Actions checkout only. The successful connection-bound
IRpcClientStreamSink and Generator contracts are deliberately unchanged.
"""
from pathlib import Path
import json
import sys

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "artifacts/issue-801-channel-shape"
OUTPUT.mkdir(parents=True, exist_ok=True)

edits = (
    (
        "src/SharpLink.Abstractions/IRpcChannel.cs",
        "public interface IRpcChannel : IRpcClientStreamSink",
        "public interface IRpcChannel",
    ),
    (
        "src/SharpLink.Client/SharpLinkClient.RpcChannel.cs",
        """    public Task SendClientStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        CancellationToken cancellationToken = default)
        => Task.FromException(new InvalidOperationException(
            "Client streams must use the connection-bound sink supplied to generated stream writers."));

""",
        "",
    ),
    (
        "src/SharpLink.Client/SharpLinkClient.DynamicChannel.cs",
        """    public Task SendClientStreamAsync<T>(long requestId, ushort streamId, IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec, CancellationToken cancellationToken = default)
        => inner.SendClientStreamAsync(requestId, streamId, stream, codec, cancellationToken);

""",
        "",
    ),
    (
        "src/SharpLink.Client/SharpLinkMetadataRpcChannel.cs",
        """    public Task SendClientStreamAsync<T>(long requestId, ushort streamId,
        IAsyncEnumerable<T> stream, IRpcCodec<T> codec, CancellationToken cancellationToken = default)
        => inner.SendClientStreamAsync(requestId, streamId, stream, codec, cancellationToken);
""",
        "",
    ),
)
changed = []
for relative_path, old, new in edits:
    path = ROOT / relative_path
    source = path.read_text(encoding="utf-8")
    count = source.count(old)
    if count != 1:
        raise RuntimeError(f"{relative_path}: expected one pattern, found {count}")
    path.write_text(source.replace(old, new), encoding="utf-8", newline="\n")
    changed.append(relative_path)
result = {
    "goal": "Unlink public IRpcChannel from IRpcClientStreamSink and remove dead-channel implementations",
    "mode": "temporary 3.0 breaking API experiment",
    "editedFiles": changed,
    "removedMethods": [
        "SharpLinkClient.SendClientStreamAsync<T> (always failed)",
        "SharpLinkModuleRpcChannel.SendClientStreamAsync<T> (forwarded to failed method)",
        "SharpLinkMetadataRpcChannel.SendClientStreamAsync<T> (forwarded to failed method)"
    ],
    "preserved": ["IRpcClientStreamSink", "ClientConnection.SendClientStreamAsync<T>",
                  "RpcGenerator generated client stream writer"],
    "warning": "Even if compilation/tests pass, this changes external IRpcChannel ABI; 3.0 release policy and public API baseline update remain necessary."
}
(OUTPUT / "channel-sink-probe.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(json.dumps(result, indent=2))
