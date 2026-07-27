# SharpLink 0.8.43 migration guide

Version 0.8.43 does not change public API, valid Protocol v2 framing, method/field IDs, or payload layouts. Application code does not require migration.

Shared-memory startup cleanup now treats only exclusively openable mappings at least one minute old as abandoned. Framework-generated random paths and the connection handshake are unchanged. Operational tooling that relied on a new connection immediately deleting another freshly created file should use explicit cleanup or wait for the stale threshold.

Connection closure without an explicit exception now retains `SharpLinkErrorCode.ConnectionClosed` instead of appearing as `Internal`. Disposing a Client response stream before observing its remote terminal marks the Activity as Error and increments `sharplink.calls.abandoned` with `consumer_abandoned`; this corrects diagnostics without changing cancellation wire bytes.

The flow-control and dynamic admission-retirement fixes are internal lifecycle changes. Valid stream credits, selectors, breaker configuration, and normal call results are unchanged. Repeated dynamic address rotation no longer retains breaker sample rings for released generations.
