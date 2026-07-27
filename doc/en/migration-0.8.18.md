# SharpLink 0.8.18 Migration Guide

Chinese: [`../migration-0.8.18.md`](../migration-0.8.18.md)

0.8.18 does not change the Protocol v2 wire format or generated Manifests. `SharpLinkFlowControlOptions.MaxConcurrentCallsPerConnection` now has a 1,048,576 hard maximum; deployments above that value should distribute concurrency across more physical connections. The 1,024 default is unchanged.

The Generic Host now calls `DisposeAsync` on its transferred Client owner whether `StopAsync(token)` succeeds, fails, or is cancelled. If Stop and Dispose both fail, it returns an `AggregateException`. Custom Clients should keep Dispose idempotent and treat it as the final token-free cleanup boundary.

Client and Server dynamic-assembly `gracefulTimeout` values and explicit send-flush `MaxLatency` values now support positive durations beyond the native timer range. `StreamManager.CompleteAll` drains every dispatcher before preserving one original failure or aggregating several. RpcSession-initiated termination isolates those user cleanup failures so pipe and transport owners are still released.
