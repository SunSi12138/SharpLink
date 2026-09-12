# Desired Session Configuration and Rolling Refresh

SharpLink keeps desired session configuration separate from active-session mutation. A server publishes one immutable desired-session snapshot, and every accepted physical connection captures exactly one snapshot at the physical accept boundary before session construction and handshake. Existing sessions remain pinned to the snapshot they captured.

## Publication and rollout intent

`ISharpLinkServer.PublishDesiredSessionAsync(...)` is the throwing convenience API. `TryPublishDesiredSessionAsync(...)` is the canonical structured control-plane path when lifecycle closure or implementation support are expected outcomes. A complete candidate is validated before publication. The first supported runtime target is `MaxFramePayloadBytes`; runtime values must remain inside the server's immutable build-time protocol ceiling.

Desired configuration generation and rollout intent are distinct state:

- `FutureOnly` may publish a new desired generation, but it does **not** advance rolling-refresh intent. Sessions already accepted against an older generation remain pinned even when their handshake completes after the FutureOnly publication.
- `RollingRefresh` marks the current desired generation as an explicit rolling target and asks capable stale sessions to replace themselves.
- Repeating `RollingRefresh` for the same desired configuration does not need to advance configuration generation. Every explicit rolling request advances a server-owned rollout request epoch; if a request arrives while the current scan is still active, the worker performs another stale-session scan before releasing ownership. This permits `FutureOnly -> same-config RollingRefresh` and retry after an interrupted caller wait without losing a re-scan request.

Once a rolling scan is started it is Server-owned. Caller cancellation cancels only that caller's wait; it does not cancel the rollout. Server shutdown is the lifetime boundary for the worker. Publication does not mutate `SharpLinkRuntimeContext` and does not add request-path locks.

The generation domain is `(ServerInstanceId, Generation)`, not a client-wide bare integer. Generations from different server processes are therefore never ordered against each other.

## SessionRefreshRequested

`SessionRefreshRequested` is a capability-gated, connection-level administrative frame. Its fixed payload contains the server instance identifier and desired generation. It carries no user metadata or arbitrary reason string.

It is intentionally different from `GoAway`:

- `GoAway` removes the current connection from new-call selection immediately and then drains it.
- `SessionRefreshRequested` keeps service capacity available while a replacement is established. Once the replacement is Ready, a blue-green eligibility cut redirects new admission to the replacement and the old connection finishes already-admitted work before entering the existing draining machinery.

Handshake catch-up follows the explicit rolling-intent generation, not merely the latest desired configuration generation. A connection pinned to G1 therefore does not receive a refresh merely because G2 was published with `FutureOnly`; if G2 is later explicitly rolled, that same stale session becomes eligible for catch-up.

A refresh request is not an endpoint failure, circuit-breaker sample, server shutdown, or client lifecycle transition. A real replacement dial/TLS/handshake/authentication failure is still diagnosed as the actual connection-attempt failure, while the healthy source session remains eligible and the rollout stays pending.

## Linearizable client admission

Ready topology snapshots are intentionally lock-free, so refresh cannot rely on the order of `MarkDraining()` and a later snapshot publication. Each selected physical connection therefore takes a lightweight admission reservation before the invocation proceeds to pending-call registration. The reservation is tracked separately from `ActiveCallCount` so ordinary load-balancing, pool expansion, and pending-capacity semantics remain unchanged; planned refresh retirement waits for both outstanding admission reservations and actual active work.

When replacement publication establishes the refresh cut, the source publishes a stable source-to-replacement redirect before closing source admission. A reader that retained an older immutable source snapshot can therefore follow that redirect to the already-Ready replacement instead of observing a false zero-ready interval. A call that already reserved the source before the cut remains formally admitted to that source and can register/complete there.

The blue-green cut is linearized on the replacement against a fatal transition the framework may already have observed. An admission reservation only proves the replacement was eligible at that instant, so the actual source-admission closure additionally claims a connection-level commit state. A replacement receive/heartbeat/disconnect failure that publishes its fatal observation first rejects the claim and rolls the replacement back with the source still selectable; a failure that lands after the successful claim is handled with ordinary post-cut replacement semantics. The source is never closed while the framework already knows the replacement is fatal.

The redirect is one shared indirection per refresh lineage rather than a predecessor chain. Every connection retired along that lineage points at the same object and that object holds only the newest Ready replacement, so the redirect graph stays constant-depth while a long-lived call keeps an old generation pinned. Repeated rapid refreshes therefore neither retain disposed predecessors nor fail admission from a stale snapshot, because a pinned source always resolves directly to the current Ready connection.

With `MaxRetiringConnections = 0`, an admitted source is hidden from new selection but remains physically Ready while its admitted work drains. It is not moved into `Draining` or counted as a retiring connection until both its admission-reservation count and `ActiveCallCount` reach zero, at which point retirement is immediate. This removes the selection-to-registration retirement race without exceeding the retiring budget.

## Bounded replacement

The client stores refresh debt per source physical connection rather than only per generation. This preserves rollout cardinality for pools with more than one old session. A supervised coordinator processes that debt with bounded jitter and one planned replacement at a time per child. Rapid duplicate/stale requests from the same server instance coalesce on that source connection.

Worker ownership is explicit. The worker's “queue is empty” decision and release of worker ownership occur under the same topology/pool lock, and an old worker only clears ownership if it still owns the matching token. A request arriving at hand-off therefore either belongs to the current worker or starts a successor; accepted refresh debt cannot be stranded with no worker.

Planned replacement has a temporary **connection-capacity** exception so `MaxConnections = 1` and `MaxConnectionsPerEndpoint = 1` can still perform replace-before-retire. It does not bypass physical-dial concurrency. Every fixed/static/dynamic replacement dial uses the same `ConnectTransportAsync(...)` boundary as initial connect/reconnect/expansion, so a multi-cluster coordinator's `MaxConcurrentClusterConnects` remains a hard aggregate limit across ordinary and refresh dials.

Static and dynamic clusters preserve source endpoint affinity. Dynamic endpoint generations that are already retiring drop their refresh debt instead of recreating retired topology. Replacement disconnect callbacks capture the published `ClientConnection` identity in a stable local; cleanup never depends on the mutable construction/cleanup variable that is nulled after ownership transfer.

## Compatibility

The capability is negotiated explicitly. Server sessions whose transport cannot support client replacement, including anonymous-pipe one-shot offers, do not negotiate it. Such peers safely fall back to future-only convergence: existing sessions remain pinned and future naturally created sessions capture the latest desired configuration. No `GoAway` or socket-close fallback is used to force refresh.
