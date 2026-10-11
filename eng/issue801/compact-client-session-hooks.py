#!/usr/bin/env python3
"""Issue #801: runner-only lazy storage trial for SharpLinkClient race hooks.

Keeps each internals-visible test-hook member name and delegate type as a
property so existing Release-mode race tests can assign lambdas unchanged.
Ordinary production client instances retain only one nullable array pointer.
"""
from __future__ import annotations
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "artifacts/issue-801-client-hooks"
OUT.mkdir(parents=True, exist_ok=True)

FIELDS = (
    ("Action", "_afterSessionRefreshEligibilitySwapTestHook"),
    ("Action<ClientConnection>", "_beforeSessionRefreshEligibilityCommitTestHook"),
    ("Action<ClientConnection>", "_afterSessionRefreshCommitReservationTestHook"),
    ("Action<ClientConnection>", "_beforeSessionRefreshCutLockTestHook"),
    ("Action<ClientConnection, ClientConnection>", "_beforeSessionRefreshCutPublicationTestHook"),
    ("Action<ClientConnection, ClientConnection>", "_afterSessionRefreshCutPublicationTestHook"),
    ("Action<ClientConnection>", "_beforeFatalFailurePublicationTestHook"),
    ("Action<ClientConnection>", "_afterFatalFailurePublicationTestHook"),
    ("Action", "_beforeSessionRefreshWorkerReleaseTestHook"),
    ("Action<ClientConnection>", "_callAdmissionReservedTestHook"),
)

changed = []


def replace_exact(s: str, old: str, new: str, label: str, count: int = 1) -> str:
    actual = s.count(old)
    if actual != count:
        raise RuntimeError(f"{label}: expected {count} occurrences, got {actual}: {old[:120]!r}")
    return s.replace(old, new)


main_path = "src/SharpLink.Client/SharpLinkClient.SessionRefresh.cs"
p = ROOT / main_path
s = p.read_text(encoding="utf-8")
old_fields = "\n".join(f"    internal {type_}? {name};" for type_, name in FIELDS)
properties = ["    // Only one reference on a production client; hook slots are allocated on test use.",
              "    private Delegate?[]? _clientTestHookSlots;", ""]
for i, (type_, name) in enumerate(FIELDS):
    properties.extend(
        [f"    internal {type_}? {name}",
         "    {",
         f"        get => ReadClientTestHook<{type_}>({i});",
         f"        set => WriteClientTestHook({i}, value);",
         "    }", ""]
    )
properties.extend(
    [
        "    private T? ReadClientTestHook<T>(int index) where T : Delegate",
        "    {",
        "        var slots = Volatile.Read(ref _clientTestHookSlots);",
        "        return slots is null ? null : (T?)Volatile.Read(ref slots[index]);",
        "    }",
        "",
        "    private void WriteClientTestHook<T>(int index, T? callback) where T : Delegate",
        "    {",
        "        var slots = Volatile.Read(ref _clientTestHookSlots);",
        "        if (slots is null)",
        "        {",
        "            if (callback is null)",
        "                return;",
        "            var allocated = new Delegate?[10];",
        "            slots = Interlocked.CompareExchange(ref _clientTestHookSlots, allocated, null) ?? allocated;",
        "        }",
        "        Volatile.Write(ref slots[index], callback);",
        "    }",
    ]
)
s = replace_exact(s, old_fields, "\n".join(properties), "client 10 field declarations")
for type_, name in FIELDS:
    s = replace_exact(
        s, f"Volatile.Read(ref {name})", name,
        f"client same-class volatile read for {name}",
        count=s.count(f"Volatile.Read(ref {name})")
    )
    if f"Volatile.Read(ref {name})" in s:
        raise AssertionError(f"field reference remains: {name}")
p.write_text(s, encoding="utf-8", newline="\n")
changed.append(main_path)

for path in (
    "src/SharpLink.Client/SharpLinkClient.StaticCluster.SessionRefresh.cs",
    "src/SharpLink.Client/SharpLinkClient.DynamicCluster.SessionRefresh.cs"
):
    f = ROOT / path
    t = f.read_text(encoding="utf-8")
    for _, name in FIELDS:
        before = f"Volatile.Read(ref _client.{name})"
        occurrences = t.count(before)
        if occurrences > 0:
            t = replace_exact(t, before, f"_client.{name}",
                              f"{path} client hook read {name}", count=occurrences)
    if t == f.read_text(encoding="utf-8"):
        raise RuntimeError(f"{path}: no test hook read changes")
    f.write_text(t, encoding="utf-8", newline="\n")
    changed.append(path)

for path in changed:
    s = (ROOT / path).read_text(encoding="utf-8")
    for _, name in FIELDS:
        if f"Volatile.Read(ref {name})" in s or f"Volatile.Read(ref _client.{name})" in s:
            raise AssertionError(f"illegal volatile property byref remains in {path}")

report = {
    "mode": "runner-only SharpLinkClient ten test-only delegate fields to one lazy Delegate[] reference",
    "source": "dev:eb99fe887cf2129d9b88441245ca0a4a6406b6c2",
    "modifiedFiles": changed,
    "oldPerClientTestFields": len(FIELDS),
    "newPerClientTestFields": 1,
    "theoreticalX64InstanceBytesSaved": (len(FIELDS) - 1) * 8,
    "compatibility": "Preserves all internals-visible C# hook names and delegate types as properties; tests can assign lambdas unchanged. No array allocation on production clients that never set hooks.",
    "nonClaims": "This is not measured GC or RPC hotpath throughput; lazy getter adds code and test callbacks allocate array when set.",
}
(OUT / "client-hook-compaction.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(json.dumps(report, indent=2))
