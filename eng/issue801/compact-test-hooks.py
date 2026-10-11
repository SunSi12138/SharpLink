#!/usr/bin/env python3
"""Issue #801: experiment, in a temporary runner checkout only.

Replaces nine per-dispatcher unit-test-only callback fields with one
lazy Action?[] reference, retaining the same internal setter interface.
No production source changes are ever committed by the companion workflow.
"""
from __future__ import annotations

import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
TARGET = ROOT / "src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs"
OUT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "artifacts/issue-801-hook-validation"
OUT.mkdir(parents=True, exist_ok=True)

NAMES = (
    "_beforeConcurrentDisposeCompletionInstallForTests",
    "_beforeRemoteTerminalPublicationPublishForTests",
    "_beforeRemoteTerminalPublicationCompletionInstallForTests",
    "_afterRemoteTerminalPublicationCompletionInstallForTests",
    "_beforeProducerOperationAcquireForTests",
    "_beforeConsumerWaitOwnerAcquireForTests",
    "_afterConsumerWaitResultForTests",
    "_beforeReturnTransitionForTests",
    "_afterReturnTransitionForTests",
)
source = TARGET.read_text(encoding="utf-8")
modified = source


def replace_exact(old: str, new: str, count: int, label: str) -> None:
    global modified
    found = modified.count(old)
    if found != count:
        raise RuntimeError(f"{label}: expected {count} occurrence(s), saw {found}")
    modified = modified.replace(old, new)


old_fields = "\n".join(f"    private Action? {name};" for name in NAMES)
replace_exact(
    old_fields,
    "    // Per-instance test instrumentation is dormant by default.\n"
    "    // The array is allocated only when a unit test installs an active callback.\n"
    "    private Action?[]? _testHooks;",
    1,
    "nine callback fields",
)

old_clear = "\n".join(
    f"        Volatile.Write(ref {name}, null);" for name in NAMES
)
replace_exact(
    old_clear,
    "        Volatile.Write(ref _testHooks, null);",
    2,
    "reset/pool return callback clearing",
)

for index, name in enumerate(NAMES):
    replace_exact(
        f"=> Volatile.Write(ref {name}, callback);",
        f"=> SetTestHook({index}, callback);",
        1,
        f"test setter {name}",
    )
    replace_exact(
        f"Volatile.Read(ref {name})?.Invoke();",
        f"InvokeTestHook({index});",
        1,
        f"test callback invocation {name}",
    )

old_retained = "\n".join(
    f"                Volatile.Read(ref {name}) is not null"
    + (" ||" if i + 1 < len(NAMES) else "")
    for i, name in enumerate(NAMES)
)
replace_exact(old_retained, "                HasTestHooks()", 1, "retained-reference diagnostic")

helper_anchor = (
    "    internal void SetAfterReturnTransitionForTests(Action? callback)\n"
    "        => SetTestHook(8, callback);"
)
helpers = """

    // A single pointer on every dispatcher instead of nine test-only pointers.
    // No array is allocated for ordinary production streams.
    private void SetTestHook(int index, Action? callback)
    {
        var hooks = Volatile.Read(ref _testHooks);
        if (hooks is null)
        {
            if (callback is null)
                return;
            var candidate = new Action?[9];
            hooks = Interlocked.CompareExchange(ref _testHooks, candidate, null) ?? candidate;
        }
        Volatile.Write(ref hooks[index], callback);
    }

    private void InvokeTestHook(int index)
    {
        var hooks = Volatile.Read(ref _testHooks);
        if (hooks is not null)
            Volatile.Read(ref hooks[index])?.Invoke();
    }

    private bool HasTestHooks()
    {
        var hooks = Volatile.Read(ref _testHooks);
        if (hooks is null)
            return false;
        foreach (var callback in hooks)
            if (callback is not null)
                return true;
        return false;
    }
"""
replace_exact(helper_anchor, helper_anchor + helpers, 1, "hook helpers insertion")

for name in NAMES:
    if name in modified:
        raise RuntimeError(f"unreplaced test-hook reference: {name}")

TARGET.write_text(modified, encoding="utf-8", newline="\n")
evidence = {
    "mode": "ephemeral PooledAsyncStreamDispatcher<T> hook field compaction",
    "originalFields": len(NAMES),
    "newFields": 1,
    "x64PointerBytesSavedPerInstance": (len(NAMES) - 1) * 8,
    "testOnlyArrayAllocation": "only upon setting non-null callback",
    "source": "src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs",
    "relevantInvariants": [
        "volatile callback setter/getter",
        "reset and pooling drop hook references",
        "all nine original setter method signatures retained",
        "all nine deterministic race callback invocation points retained",
    ],
}
(OUT / "hook-compaction-probe.json").write_text(
    json.dumps(evidence, indent=2) + "\n", encoding="utf-8"
)
print(json.dumps(evidence, indent=2))
