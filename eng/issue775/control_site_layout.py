"""Mechanical method extraction; the original bodies are hash-locked and unchanged."""
import hashlib
from pathlib import Path
import subprocess

BODY_SHA = "2ae3aaffe3f14ce7cf131ecf42127f7e992050616e409e74056e567831e9d138"
INV = "src/SharpLink.Client/SharpLinkClient.Invokers.cs"
NEW = "src/SharpLink.Client/SharpLinkClient.LogicalInvocation.cs"
START = "    private ValueTask<T> CompleteLogicalInvocation<T>"
END = "    private IAsyncEnumerable<TResponse> InvokeDuplexStreamingCore"


def apply_layout(root, driver):
    before = (root/INV).read_text()
    if before.count(START) != 1 or before.count(END) != 1 or (root/NEW).exists():
        raise ValueError("Layout anchor or destination changed")
    a, b = before.index(START), before.index(END)
    original_block = before[a:b]
    if hashlib.sha256(original_block.encode()).hexdigest() != BODY_SHA:
        raise ValueError("Logical-invocation bodies changed")
    logical = "namespace SharpLink.Client;\n\ninternal sealed partial class SharpLinkClient\n{\n" + original_block.rstrip() + "\n}\n"
    (root/INV).write_text(before[:a] + before[b:])
    (root/NEW).write_text(logical)
    subprocess.run(["git", "add", INV, NEW], cwd=root, check=True)
    # Reading back prevents an incomplete or modified move from being accepted.
    if (root/NEW).read_text() != logical or (root/INV).read_text() != before[:a] + before[b:]:
        raise ValueError("Incomplete method extraction")
    return {"transformSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "newFile": NEW, "movedBodySha256": BODY_SHA, "methodBodiesUnchanged": True,
            "invokersLines": len((root/INV).read_text().splitlines())}
