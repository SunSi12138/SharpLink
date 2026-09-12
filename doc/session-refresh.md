# Desired Session Configuration and Rolling Refresh

SharpLink keeps runtime session configuration separate from active-session mutation.
A server publishes one immutable desired-session snapshot, and every accepted physical
connection captures exactly one snapshot before session construction and handshake.
Existing sessions remain pinned to the snapshot they captured.

## Publication

`ISharpLinkServer.PublishDesiredSessionAsync(...)` validates a complete candidate before
publication. The first supported runtime target is `MaxFramePayloadBytes`; runtime values
must remain within the server's immutable build-time protocol ceiling.

`FutureOnly` publishes the new generation without touching existing sessions.
`RollingRefresh` additionally asks eligible older sessions to replace themselves.
Publication does not mutate `SharpLinkRuntimeContext` and does not add request-path locks.

The generation domain is `(ServerInstanceId, Generation)`, not a client-wide bare integer.
Generations from different server processes are therefore never ordered against each other.

## SessionRefreshRequested

`SessionRefreshRequested` is a capability-gated, connection-level administrative frame.
Its fixed payload contains the server instance identifier and desired generation. It carries
no user metadata or arbitrary reason string.

It is intentionally different from `GoAway`:

- `GoAway` removes the current connection from new-call selection immediately and then drains it.
- `SessionRefreshRequested` keeps the current connection Ready while a replacement is established.
  Only after the replacement is Ready does the old connection enter the existing draining machinery.

A refresh request is not an endpoint failure, circuit-breaker sample, server shutdown, or
client lifecycle transition. A real replacement dial/TLS/handshake/authentication failure is
still diagnosed as the actual connection-attempt failure, while the healthy source session
remains eligible and the rollout stays pending.

## Bounded replacement

The client stores refresh debt per source physical connection rather than only per generation.
This preserves rollout cardinality for pools with more than one old session. A single supervised
coordinator processes that debt with bounded jitter and one planned replacement at a time.
Rapid duplicate/stale requests from the same server instance coalesce on that source connection.

Planned replacement has a temporary capacity exception so `MaxConnections = 1` and
`MaxConnectionsPerEndpoint = 1` can still perform replace-before-retire. The exception is not
used by ordinary expansion or reconnect paths. Static and dynamic clusters preserve source
endpoint affinity. Dynamic endpoint generations that are already retiring drop their refresh
debt instead of recreating retired topology.

Retiring connections remain bounded. When the retiring budget is exhausted (including an
explicit zero budget while a source still has active work), the source stays Ready and refresh
waits. Long-lived unary or streaming work therefore follows the existing drain semantics rather
than being aborted solely for configuration rollout.

## Compatibility

The capability is negotiated explicitly. Server sessions whose transport cannot support client
replacement, including anonymous-pipe one-shot offers, do not negotiate it. Such peers safely
fall back to future-only convergence: existing sessions remain pinned and future naturally
created sessions capture the latest desired configuration. No `GoAway` or socket-close fallback
is used to force refresh.
