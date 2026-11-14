# ADR 0003: One contract-scoped receiver and Session-local binding

- Status: Proposed for 3.0; architecture selected in [#775](https://github.com/SunSi12138/SharpLink/issues/775), final API/package review in [#805](https://github.com/SunSi12138/SharpLink/issues/805).
- Related: [channel migration](../rpc-channel-3.0-migration.md), [wire grammar](../protocol-v2.md), [implementation/evidence](../issue-775-production-integration.md).

## Context

A local proxy must not depend on whichever remote Manifest arrived first. Full identity validation
belongs on the actual data Session, while every business request still needs exact registration
admission and complete method facts. Per-call identity lookup, a client-wide default identity or
latest resolution after a redirect can add work or combine different Session/generation owners.

## Decision

Use one generated/manual contract-channel path. A synchronous local factory returns a retained
receiver with one validated local generation and immutable scope. Generated callers get Service
and finite Prepare on that same receiver; manual callers use a client-issued RpcLocalContract.
Remove the raw channel and old Get factories, without a compatibility facade or default identity.
Keep RuntimeContext and typed Codec/writer SPIs.

The cold controller coalesces bounded Bind demands. An immutable confirmed snapshot contains
matching topology, exclusions and inseparable Session/token pairs. Existing Picker/policy reserves
that physical Session. Reconnect/blue-green replacements obtain their own binding. Unbound calls
fail fast before serialization/producer work; Prepare neither reserves a call nor upgrades a
retired local generation.

Server Bind validates full identity/receipt and installs an entry for one exact registration before
Ack. Every request compares its full token, protects that registration and uses its immutable
complete facts through the original invocation/cleanup pipeline. Group replacement closes old
admission before publishing its successor. Pre-cut queued work retains R1 through physical exit;
later work cannot reacquire R1 or retarget the same attempt to R2. Bounded idle bindings detach
collectible payload after physical owners exit.

Two-way producers also wait for this call's RequestAccepted and the original pending generation's
start claim. OneWay has no per-call Ack; an independent bounded input count permits rejection to
drain/return credit without invoking an unknown or retired Codec.

Server receipt-bearing Register/Replace results capture immutable scalar receipts at publication,
before waiting for old work to drain. Keep the original shared registry entry points/result meaning.
The separate current-state getter serves already-running/static registrations, not identification
of an earlier operation. Neither receipt nor token grants authorization or proves physical cleanup.

## Consequences and alternatives

API6 and the exact contract-binding ABI reject API4/API5 generated output; wire minor/floor5 rejects
unbound minor4 requests. Rebuild the complete Proxy/Stub/bootstrap/Codec closure and migrate callers
together. Numeric grammar/quotas remain in protocol source and documentation.

Cold handles, weak-demand tracking, control state, Bind round trips and two-way acceptance have
real costs. Startup/steady latency, allocations and supported NativeAOT paths require measured
comparison; this architecture does not approve a regression.

A raw/default-identity facade adds a second selection state and fallback path. Per-call full
identity repeats identity handling. A latest-registration alias invalidates exact receipts and
original Codec/CTS ownership. None is retained. Third-party public channels represent their own
implementation, not a capability to inject a Session/token into the built-in client.
