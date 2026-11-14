"""Check that the active private server cannot fall back to a Stub/latest-registry resolver."""
import re
from pathlib import Path

root = Path(__file__).resolve().parents[2]


def profile(source):
    output, stack = [], [True]
    for line in source.splitlines():
        directive = line.strip()
        if directive in ("#if SHARPLINK_775_STATIC_SLICE", "#if !SHARPLINK_775_STATIC_SLICE"):
            stack.append(directive == "#if SHARPLINK_775_STATIC_SLICE")
        elif len(stack) > 1 and directive == "#else":
            stack[-1] = not stack[-1]
        elif len(stack) > 1 and directive == "#endif":
            stack.pop()
        elif all(stack):
            output.append(line)
    assert len(stack) == 1
    return "\n".join(output)


resolvers = []
for path in (root / "src/SharpLink.Server").rglob("*.cs"):
    if "obj" in path.parts or "bin" in path.parts:
        continue
    source = profile(path.read_text(encoding="utf-8"))
    assert not re.search(r"\b(?:TryGetMethodDescriptor|GetMethodDescriptor|SupportsCancellation)\s*\(", source), path
    resolvers.extend([path.name] * len(re.findall(r"\.TryResolveMethod\s*\(", source)))
assert resolvers == ["StaticBindingTable.cs"], resolvers
print("PASS facts source: one cold Bind resolver; no narrow/fallback server query in the active profile")
