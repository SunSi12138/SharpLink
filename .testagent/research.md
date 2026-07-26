# 0.8.16 regression-test research

## Target inventory and evidence candidates

- `PendingRequestTable`: unlike the Server deadline scheduler, its timer arms the full remaining duration. A valid deadline beyond the platform `Timer` range throws while registering an otherwise valid call.
- `SharpLinkBufferWriterPool` / `SharpLinkRuntimeContext`: returned writers can retain up to the configured pool budget, but Context disposal neither drains the pool nor prevents later rents, so a disposed Client/Server object can retain large arrays indefinitely.
- `SharpLinkServer.StopCoreAsync`: an immediately faulted listener/framework-cleanup task is logged and converted only to `HealthStatus == Unhealthy`; the public Stop/Dispose operation reports success and hides the owned-resource cleanup failure.
- `SharpLinkServerHostedService`: the long-lived Run loop is linked to the transient `StartAsync` cancellation token. Canceling that token after startup silently stops a healthy server.
- `SharpLinkProtocolOptions.MaxPendingRequestsPerConnection`: any positive power of two is accepted, including values that require multi-gigabyte arrays for every physical Client connection.

## Acceptance checklist

- Client deadline scheduling slices delays beyond the portable timer maximum without changing short-deadline behavior.
- Context disposal drains idle writer buffers, rejects new rents, and releases active writers when they are returned after disposal.
- Server Stop/Dispose preserves immediate framework cleanup failures while still reaching the faulted terminal state and completing all cleanup layers.
- Hosted Server startup honors cancellation during startup but does not retain the startup token after successful publication.
- Pending request capacity has a documented power-of-two hard maximum enforced both by public protocol validation and the table constructor.

## Audit guardrails

These findings cover timer correctness, bounded memory ownership, cleanup observability, and Generic Host lifecycle semantics. Cosmetic syntax changes, arbitrary tighter defaults, and the larger anonymous-pipe transfer API redesign are excluded from this batch pending a separately bounded compatibility design.
