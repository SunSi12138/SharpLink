#!/usr/bin/env python3
"""Guard known retired architecture patterns; semantic race tests remain required."""

from pathlib import Path
import re

root = Path(__file__).resolve().parent.parent
forbidden = {
    "process-default runtime": r"SharpLinkRuntimeContext\s*\.\s*Default\b",
    "process-default runtime declaration": r"\bstatic\s+SharpLinkRuntimeContext\s+Default\b",
    "two-phase runtime/telemetry binding": r"\b(?:BindRuntimeContext|SetTelemetrySide)\b",
    "public runtime engine": r"\bpublic\s+(?:(?:sealed|partial|abstract)\s+)*(?:class|interface)\s+(?:RpcSession|IRpcSession|StreamManager|IStreamManager|IStreamDispatcher|PooledAsyncStreamDispatcher)\b",
    "legacy generated ABI adapter": r"\b(?:LegacyRpcChannelAdapter|LegacyGeneratedAbiAdapter|ReflectionManifestAdapter)\b",
}
# Reviewed remaining yield sites. Changes require a new path/reason review; these are not
# consumer-abandon detach polling. Per-test interleaving/ownership assertions cover their semantics.
yield_sites = {
    "src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs": (1, "MoveNext waits for an admitted producer's publication after completion"),
    "src/SharpLink.Runtime/Transport/TransportConnection.cs": (1, "defer inline read cancellation outside the state gate"),
    "src/SharpLink.Server/SharpLinkServer.ContractManifest.cs": (2, "supervised coalescing worker and registry-generation fairness"),
    "src/SharpLink.Server/SharpLinkServer.AssemblyDrain.cs": (1, "one cancellation-continuation turn before supervised drain cleanup"),
    "src/SharpLink.Client/ClientAssemblyRegistry.cs": (1, "one cancellation-continuation turn before supervised drain cleanup"),
    "src/SharpLink.Server/SharpLinkServer.DesiredSession.cs": (1, "start the supervised rollout worker outside its owner gate"),
}
utc_sites = {
    "src/SharpLink.Runtime/RpcSession.cs": (2, "public diagnostic last-active UTC timestamps"),
    "src/SharpLink.Server/SharpLinkServer.cs": (1, "public health timestamp"),
    "src/SharpLink.Client/SharpLinkClient.SupportSnapshot.cs": (2, "support capture/failure UTC timestamps"),
    "src/SharpLink.Runtime/Transport/SharedMemoryMapping.cs": (1, "compare filesystem mapping age against filesystem UTC metadata"),
    "src/SharpLink.Abstractions/SharpLinkAuthenticationContext.cs": (1, "externally defined absolute authentication expiry"),
}
patterns = {
    "yield": (r"\bTask\.Yield\s*\(", yield_sites),
    "UTC": (r"\b(?:DateTime(?:Offset)?\.UtcNow|GetUtcNow\s*\()", utc_sites),
}
seen = {kind: set() for kind in patterns}
count = 0
for path in sorted((root / "src").rglob("*.cs")):
    if "bin" in path.parts or "obj" in path.parts:
        continue
    count += 1
    relative = path.relative_to(root).as_posix()
    source = path.read_text(encoding="utf-8-sig")
    for name, pattern in forbidden.items():
        if re.search(pattern, source):
            raise ValueError(f"Retired {name} reintroduced in {relative}")
    for kind, (pattern, allowed) in patterns.items():
        matches = len(re.findall(pattern, source))
        if matches:
            expected, reason = allowed.get(relative, (0, "no reviewed exception"))
            if matches != expected:
                raise ValueError(f"Unreviewed {kind} sites in {relative}: {matches}; {reason}")
            seen[kind].add(relative)
for kind, (_, allowed) in patterns.items():
    if seen[kind] != set(allowed):
        raise ValueError(f"Stale {kind} allowlist entries: {set(allowed) - seen[kind]}")
print(f"Verified architecture regression guards across {count} source files and explicit yield/UTC exceptions.")
