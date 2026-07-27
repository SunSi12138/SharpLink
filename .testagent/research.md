# 0.8.29 regression-test research

## Bounded target inventory

- `PendingRequestTable`: `Rent` checks disposal only before insertion, while both stream registration APIs can insert without any disposal check. A 50,000-iteration external race probe witnessed a stranded slot on iteration 1 (`requestId=1`, incomplete operation).
- Client/server heartbeat expiry uses `DateTime.UtcNow` and mutable wall-clock `IRpcSession.LastActive`; a clock rollback or future timestamp can prevent timeout, while a forward adjustment can disconnect a healthy peer.
- Named-pipe and shared-memory logical names accept path separators and NUL, producing platform-dependent or delayed transport failures instead of synchronous configuration rejection.
- Socket endpoint snapshotting rebuilds Unix-domain endpoints from `ToString()`. For an abstract Linux endpoint, the original serialized path starts with NUL (`15 01 00...`) while the snapshot starts with `@` (`16 01 40...`), changing it into a filesystem endpoint. Listener cleanup likewise treats the display string as a filesystem path.
- `SharpLinkMultiClusterClient.State` uses LINQ over a frozen dictionary and allocates 56 bytes on every Ready/Degraded read; a one-million-read probe measured exactly 56,000,000 bytes.

The repository convention is TUnit in `test/SharpLink.UnitTests`. The required source/test pairing scan was run; retained ignored A/B baseline clones under `artifacts/` pollute its global counts, so it is used only as a routing heuristic. Focused regression coverage belongs in `RequestManagerTests`, `SharpLinkClientLifecycleStateTests`, `TransportValidationTests`, and `SharpLinkMultiClusterClientTests`.

## Acceptance checklist

- Calls begun after pending-table disposal throw synchronously; insertion racing with disposal is terminally completed and cannot leave a slot or incomplete operation.
- Heartbeat timeout decisions use monotonic elapsed time and cannot be defeated by a future public wall-clock `LastActive` value.
- All pipe-backed logical-name entry points reject separators and NUL during construction.
- Unix-domain endpoint snapshots preserve serialized bytes, and abstract endpoints never participate in filesystem ownership/deletion checks.
- Multi-cluster state reads preserve semantics while allocating zero bytes after warm-up.
- Existing behavior remains covered and runtime performance does not materially regress.

## Audit guardrails

The public `IRpcSession.LastActive` wall-clock property remains available for compatibility and diagnostics; only timeout accounting moves to an internal monotonic timestamp. Pipe names remain logical identifiers, so cross-platform rejection is intentional. Abstract-socket handling is limited to byte-preserving snapshot and cleanup ownership, without changing bind semantics.

## Rejected hypothesis

The two ready-signal completions initially suspected to be duplicate belong to the independent fixed-client and static-cluster shutdown paths. Neither path contains a duplicate operation, so no cleanup change is warranted.

## P3 cleanup

The server receive loop already marks every parsed frame active before dispatch. Its Ping case repeated the same wall-clock write; after monotonic sampling this would duplicate both clocks, so the redundant Ping-only update is removed without advancing the version batch.
