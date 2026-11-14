"""Check that the ordinary server cannot fall back to a Stub/latest-registry resolver."""
import re
from pathlib import Path

root = Path(__file__).resolve().parents[2]


resolvers = []
for path in (root / "src/SharpLink.Server").rglob("*.cs"):
    if "obj" in path.parts or "bin" in path.parts:
        continue
    source = path.read_text(encoding="utf-8")
    assert "SHARPLINK_775_STATIC_SLICE" not in source, path
    assert not re.search(r"\b(?:TryGetMethodDescriptor|GetMethodDescriptor|SupportsCancellation)\s*\(", source), path
    resolvers.extend([path.name] * len(re.findall(r"\.TryResolveMethod\s*\(", source)))
assert resolvers == ["StaticBindingTable.cs"], resolvers
print("PASS facts source: one cold Bind resolver; no narrow/fallback server query in the ordinary product")
