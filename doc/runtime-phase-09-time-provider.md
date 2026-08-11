# Runtime Architecture Phase 09: Unified time ownership

Runtime control flow uses the `TimeProvider` owned by its `SharpLinkRuntimeContext`. Client and
Server builders may replace the default `TimeProvider.System`; a provider is then immutable for the
context lifetime. Two contexts therefore have independent clocks, timers, and cancellation
boundaries.

## Migrated control flow

| Area | Provider-owned state and waits | Boundary rule |
|---|---|---|
| Session and heartbeat | activity timestamps, Ping payload timestamp, send-pump batching, client and Server heartbeat loops | activity elapsed time is monotonic; entering the client heartbeat loop still sends its first Ping immediately |
| Client lifecycle | handshake timeout, fixed/static/dynamic reconnect, resolver retry, readiness stability, retry and interceptor elapsed time | reconnect jitter is an internal reconnect-only seam; deadlines use one shared monotonic boundary |
| Resilience | circuit-breaker sampling/open duration and built-in delegate/DNS resolver polling | built-in resolvers bind once to their owning client context; custom resolvers remain application-owned |
| Admission | queue timeout, deadline comparison, queue elapsed time and partition idle reclamation | equality is terminal for a deadline or maximum queue wait; partition timestamp zero is valid |
| Dynamic lifecycle | client/Server assembly drain, dynamic-module drain, multi-cluster retired-client drain and deferred unregister | a retired child uses that child's provider; the multi-cluster coordinator uses the first child provider |
| Server lifecycle | handshake, graceful stop, force cleanup, framework/service cleanup and one-way log throttling | cleanup budgets use monotonic deadlines; diagnostic stop time uses the same provider's UTC clock |

`RpcDeadline` and `SharpLinkTimer` contain the overflow-safe conversion and provider-aware timer
primitives used by these paths. Large durations saturate instead of wrapping or depending on the
platform timer range.

## `LastActive` compatibility and hot path

The Runtime-internal Session activity snapshot records both the context provider's current UTC value
and its monotonic timestamp. The two values deliberately serve different purposes: UTC activity is
diagnostic wall-clock state, while heartbeat and timeout decisions use only the monotonic timestamp.
It is no longer a public mutable Session API.

An alternative implementation that projected `LastActive` from an immutable UTC/monotonic anchor
was measured because it removes the UTC read from `MarkActive`. Although that candidate improved
the isolated activity-update benchmark, clean interleaved end-to-end runs did not show a stable RPC
benefit and sometimes moved CPU or P99 in the wrong direction. In accordance with issue #94's
acceptance rule, the candidate was rejected instead of retaining extra state and altered diagnostic
semantics. UTC jumps can change the next diagnostic `LastActive` value, but cannot shorten or extend
heartbeat timeouts.

## Allowed clocks outside RuntimeContext control

The source timing audit intentionally retains these uses:

| Use | Reason | Owner / terminal behavior |
|---|---|---|
| `SharpLinkTelemetry` `Stopwatch` timestamps | measurement only; never controls an RPC state transition | an Activity/Meter scope ends with the observed call |
| `SharpLinkAuthenticationContext` optional `DateTimeOffset.UtcNow` | absolute credential expiry; callers may pass an explicit `now` | authentication policy, not a Runtime timer |
| `SharedMemoryMapping` `DateTime.UtcNow` | compares an OS file modification time while reclaiming stale mappings | shared-memory transport setup |
| TLS and shared-memory transport handshake timeout sources | transport options exist below a `SharpLinkRuntimeContext` | the transport owns and disposes each timeout source |
| shared-memory writer cleanup wait | bounded best-effort transport teardown | the control channel owns the writer task and teardown budget |
| application-defined endpoint resolvers | user code owns its scheduling contract | built-in SharpLink resolvers are provider-bound; custom implementations are not rewritten |

Adding new `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, global `Stopwatch` control flow, provider-less
`Task.Delay`, or timeout `CancellationTokenSource` calls to context-owned Runtime, Client, or Server
paths is outside this ownership model.

## Verification

Deterministic tests advance manual providers across one tick before, exact equality, and after each
boundary. They also verify timer disposal, pending-call/stream/queue counters, stop/cancel races, and
two-provider isolation. Existing System-time integration tests remain the compatibility gate.

Performance validation pairs direct `MarkActive`, `MarkActive` plus `LastActive`, send-pump, circuit
breaker, and admission microbenchmarks with tiny Unary, Server-stream, and Duplex workloads. Results
are compared on the same Ubuntu host and exact revisions; a timing optimization is retained only
when managed allocation does not regress and workload measurements show a stable benefit.

On the .NET 10.0.10 Ubuntu comparison host, the rejected UTC-anchor candidate improved isolated
`MarkActive` from 32.72 ns to 16.66 ns and `MarkActive` plus `LastActive` from 33.43 ns to 19.53 ns,
with zero managed allocation in both revisions. The projected diagnostic getter measured 1.49 ns,
where the previous auto-property getter was below BenchmarkDotNet's resolution. These numbers prove
the isolated cost exists, but not that removing it improves RPC workloads.

After unrelated long-running processes were removed, five interleaved baseline/candidate tiny-Unary
pairs varied from -6.07% to +2.32% in throughput. Their medians were about +1.8% throughput, +0.8%
P99 latency, and +3.7% CPU time for the candidate. That mixed result is within host/process noise and
is not a stable end-to-end improvement. The retained implementation therefore preserves the simple
UTC diagnostic state and applies only the unified provider/monotonic timeout work required by this
phase. A final direct rerun of the retained implementation measured 32.65 ns for `MarkActive` and
32.97 ns for `MarkActive` plus `LastActive`, versus 32.72 ns and 33.43 ns at the exact baseline; all
four measurements allocated zero managed bytes. The benchmark cases remain as regression evidence
for future proposals.
