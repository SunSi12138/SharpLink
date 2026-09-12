# Client reconnect policy

SharpLink reconnect timing is owned by the existing Client topology lifecycle rather than by logical-RPC retry. Issue #592 makes those rules explicit without introducing a second reconnect coordinator.

## Current-state / compatibility matrix

| Topology | Reconnect owner | Initial delay | Backoff | Max delay | Jitter | Stable reset | Time / wake owner |
| --- | --- | ---: | --- | ---: | --- | --- | --- |
| fixed transport / one static endpoint | `SharpLinkClient.ReconnectLoopAsync` | 100 ms | x2 | 5 s | 0.8x..1.2x | after 30 s continuously Ready | Client `TimeProvider` + `SharpLinkTimer`; existing reconnect signal owns the loop and policy generation wakes only an armed delay |
| multi-endpoint static cluster | `StaticClusterRuntime`, at most one `ReconnectTask` for an admitted endpoint | 100 ms | x2 | 5 s | 1.0x..1.25x | immediate after a successful reconnect | Client `TimeProvider` + `SharpLinkTimer`; existing per-endpoint task owns scheduling and the policy generation wakes only its armed delay |
| dynamic resolver cluster | `DynamicClusterReconnectCoordinator`, scoped to the current endpoint generation | 100 ms | x2 | 5 s | 1.0x..1.25x | immediate after a successful reconnect | Client `TimeProvider` + `SharpLinkTimer`; current endpoint-generation task owns scheduling and the policy generation wakes only its armed delay |

Resolver observation/retry is a separate resolver lifecycle. Reconnect policy does not change resolver watch scheduling.

## Policy model

`SharpLinkReconnectPolicy` is one immutable replacement unit containing:

- `InitialDelay`
- `MaxBackoff`
- `BackoffMultiplier`
- `JitterMinimumFactor` / `JitterMaximumFactor`
- `StableResetWindow`

`SharpClientBuilder.UseReconnectPolicy(...)` configures the initial complete policy. `ISharpLinkClient.GetReconnectPolicy()` returns the currently published policy and `UpdateReconnectPolicy(...)` atomically replaces it for the running Client.

If no explicit policy is supplied, the matrix above is materialized so existing topology behavior remains compatible. Once an explicit policy is supplied, the same field semantics apply to every built-in reconnect topology.

## Runtime reconciliation semantics

Policy and reconnect state remain separate. Live state consists of the topology-owned failure/backoff position, Ready timestamp/history, currently armed wait, and the existing reconnect task/loop. Publishing a new policy does **not** zero or recreate any of those state owners.

An update publishes one complete generation and signals the previous generation. If a reconnect delay is armed, only that delay is cancelled. Its existing coordinator loops, captures the newest complete generation, reconciles the stored backoff position to the new bounds, and arms one replacement wait. No second timer loop or eager connection attempt is created.

The stored backoff position is reconciled as follows:

- a positive existing position is retained when it is inside the new `[InitialDelay, MaxBackoff]` range;
- decreasing `InitialDelay` therefore does not move an active failure streak backwards;
- increasing `InitialDelay` clamps an older smaller position to the new lower bound instead of treating the update as a fresh failure sequence;
- decreasing `MaxBackoff` clamps an older larger position to the new cap;
- increasing `MaxBackoff` leaves the existing position unchanged and lets later failures continue from it;
- a reconnect failure advances from the active position using the latest `BackoffMultiplier` and `MaxBackoff`;
- the latest jitter bounds are applied each time a wait is newly armed.

A transport `ConnectAsync` that already crossed the dial boundary is not cancelled or restarted by policy publication. If one or more policy generations are published while that attempt is in flight, the attempt is still the sole owner. When it completes, its success/failure transition is reconciled against the newest policy generation; any subsequent wait captures that latest policy.

`StableResetWindow` never manufactures stability. Existing Ready timestamps are preserved across updates and are evaluated against the policy effective when a later disconnect decides whether the backoff sequence is stable enough to reset. A zero window retains the legacy immediate-reset behavior for static/dynamic clusters.

Initial connection attempts are deliberately outside reconnect policy. Logical RPC retry remains independent and continues to use its own retry policy/state. Ordinary RPC invocation does not read reconnect policy, so publication remains a lifecycle/control-plane operation.
