# Generation lifecycle contract

SharpLink uses one lifecycle vocabulary for generation-style replacement:

```text
Prepare -> Validate -> Publish -> Retire -> Drain -> Reclaim
```

`Publish` is the commit point. Before it, a candidate is private and candidate-owned resources may be rolled back. After it, the published generation is committed: new eligible work observes the new generation, the old generation retires, and cleanup remains framework-owned even if the initiating caller stops waiting.

This is a semantic contract, not a requirement that assemblies, clusters, endpoints, and physical connections share one state machine.

## Canonical invariants

### Prepare and Validate

- Candidate construction is not externally visible.
- Validation completes against the generation that will be replaced before publication.
- Pre-publish failure or caller cancellation releases candidate-owned resources exactly once.
- A failed candidate cannot mutate or partially publish the current generation.

### Publish

- Publication is one atomic subsystem boundary: registry/snapshot readers see either the previous generation or the committed replacement.
- Caller cancellation after publication cancels only that caller's observation of retirement; it cannot roll back the committed generation.
- Cleanup failure after publication is reported/observed as cleanup failure and never resurrects the retired generation.

### Retire, Drain, Reclaim

- Retirement stops new ownership where the subsystem has an acquisition boundary.
- Existing calls/streams/leases keep their old generation dependencies alive.
- Reclaim is driven by explicit ownership becoming empty, or by the subsystem's documented forced-drain policy. It is not driven by an arbitrary delayed-dispose timer.
- Reclaim/dispose is exact-once for generation-owned resources.

`SharpLinkRetirementHandle` is the small shared primitive for post-publish caller observation. It wraps framework-owned retirement work without linking caller cancellation to that work. Dynamic-module unregister and multi-cluster slot retirement use it. It deliberately does not own candidate rollback, validation, leases, or subsystem-specific force policy.

## Characterization

| Lifecycle fact | Dynamic assembly/module replacement | Multi-cluster `ReplaceClusterAsync` | Resolver endpoint generation replacement | Connection retirement / GoAway |
| --- | --- | --- | --- | --- |
| Candidate owner | The replacing client/server call owns the new manifest, generated-codec registration, and `SharpLinkDynamicModule` until publication. | The mutation owns `SharpLinkPreparedCluster` / replacement child until the slot snapshot is published. | The dynamic-cluster topology mutation owns newly created `DynamicEndpointState` objects and transport factories until the resolver snapshot is committed. | GoAway itself has no replacement candidate. Pool expansion/reconnect owns any newly dialed `ClientConnection` until it is admitted to the endpoint's ready set. |
| Validation boundary | Manifest compatibility, dependency/conflict checks, and the exact registry generation are revalidated under `_registryGate` immediately before publication. | Exact old slot identity, route compatibility, steady budget, and bounded transition budget are revalidated under `_gate` immediately before the snapshot write. | Snapshot version/endpoint identity, transport-factory ownership, and generation-preserving vs generation-replacing changes are validated under the dynamic-cluster gate before `CommitCurrent`. | The connection/session must still be current and Ready. `MarkDraining` is a Ready -> Draining CAS; protocol/frame validation occurs before a received GoAway is acted on. |
| Publication / commit point | New codecs plus proxy/service registry state are published under `_registryGate`; the registry generation advances. From this point the replacement is committed. | `Volatile.Write(ref _snapshot, new MultiClusterSnapshot(...))` under `_gate` is the externally visible commit; dynamic registrations and transition budget are changed in the same serialized mutation. | `CommitCurrent` changes the current endpoint-generation map under the topology writer gate; ready/selection snapshots are then published atomically to readers. | `ClientConnection.MarkDraining` publishes Draining and calls `Session.MarkDraining`; endpoint/pool readiness publication removes the connection from new selection. |
| New-work visibility rule | Proxy/service lookup sees the newly published registry snapshot. The retired module is no longer the owner of newly routed work. | Routing reads the immutable published `MultiClusterSnapshot`; new calls select the replacement slot. | Selection reads the volatile endpoint selection snapshot. Address/Authority change creates a new generation; attributes-only change preserves the existing generation/connections. | `CanAcceptCalls` becomes false in Draining and selection no longer offers the connection. New calls choose another Ready connection/endpoint when available. |
| Old-work ownership rule | `SharpLinkDynamicModuleLease` and its striped call/stream counters explicitly retain the old module and its generated runtime resources. | The retired child client remains owned by the coordinator while its explicit active-call/active-stream counts are non-zero; transition connection budget remains charged until cleanup finishes. | Retired `DynamicEndpointState` keeps its old physical connections; each connection's pending calls/streams explicitly own the session until they complete or are forced. | `PendingRequestTable`, stream state, and auxiliary active-call accounting keep the physical connection alive for already admitted work. |
| Retirement trigger | Publication calls `TryBeginDraining` on the old module; unregister uses the same transition. | Immediately after snapshot publication the old slot is wrapped in framework-tracked retirement cleanup. | Endpoint removal or same-ID Address/Authority change marks the old endpoint generation retiring and removes it from current selection. | Received GoAway, endpoint-generation retirement, pool resize, stop, or dispose calls `MarkDraining`/retirement logic. |
| Drain condition | `SharpLinkDynamicModule.WaitForDrainAsync` completes when the final relevant module lease is released. | Active call and stream counts reach zero, or the graceful drain budget expires; the child is then stopped. | Retired endpoint connections have no remaining admitted calls/streams and their reconnect/expansion ownership is detached. | `ActiveCallCount` reaches zero for graceful retirement; pending stream/call ownership is therefore empty. |
| Forced-drain behavior | On graceful timeout the module's forced-cancellation token is cancelled. If leases remain, the result reports `ReferencesReleased = false` plus remaining counts and deferred cleanup waits for final release. | Graceful timeout ends the active-call wait and `StopAsync` forces the retired child toward termination. A bounded caller wait may report cleanup still pending while framework cleanup continues. | Stop/dispose closes retiring generations; retiring-connection budget enforcement may select excess retirees for immediate termination rather than accepting unbounded old generations. | Stop/dispose or retiring-budget enforcement fails/terminates the connection and pending work using the existing stable connection-closed semantics. |
| Caller-cancellation semantics | Before publication, cancellation may abandon candidate work. After publication, the shared retirement operation remains framework-owned; caller cancellation only cancels `SharpLinkRetirementHandle<T>.WaitAsync`. | Before publication, the candidate is stopped during rollback. After publication, caller cancellation only stops `SharpLinkRetirementHandle` observation; the old child remains retired and tracked. | Resolver updates are framework-owned rather than per-update caller-owned. Client shutdown is the lifetime boundary; a resolver failure/cancellation cannot roll back an already accepted snapshot. | GoAway is a framework/protocol event, not a caller-owned mutation. Cancellation of an individual call affects that call, not the connection's committed Draining state. |
| Cleanup owner | Client/server framework-task supervision owns the unregister/replacement drain and any timed-out deferred release. | Multi-cluster framework-task supervision owns retired-slot cleanup and holds transition budget until its `finally` completes. | `DynamicClusterRuntimeLifecycle` owns retirement/connect/resolver workers and factory/connection disposal. | Endpoint/cluster runtime owns retiring-connection cleanup; the connection owns pending-call/stream failure and session disposal. |
| Cleanup-failure semantics | The shared unregister operation faults and framework supervision observes it. Published registry state is not rolled back; release still marks the module Released in cleanup `finally` paths. | A waiting caller can observe cleanup failure and framework supervision also observes the task. `_transitionConnectionBudget` is released in `finally`; the new snapshot is never reverted. | Cleanup failures are supervised/logged or aggregated during stop. They do not reinsert the retired endpoint generation or replace the accepted topology with a stale one. | Cleanup/failure closes the connection and reports through existing supervision/logging; it cannot transition Draining back to Ready. |

## Why endpoint and connection state remain separate

Endpoint generation and physical connection retirement participate in selection and call-admission hot paths. They already have explicit generation identity, Ready/Draining state, pending-call ownership, and active-call accounting, and there is no post-publication user waiter to detach from cleanup. Wrapping those objects in a generic lease/state framework would add indirection without removing state. Their implementation therefore remains subsystem-specific while following the same commit/retire/drain contract.

Dynamic modules also retain their existing striped lease implementation because it is the ownership authority that makes collectible ALC reclamation deterministic. `SharpLinkRetirementHandle` is intentionally only an observation boundary and does not replace that lease.

Multi-cluster retirement is similarly tied to explicit child active-call/active-stream ownership. Its provider-driven polling observes that condition; the graceful timeout controls forced drain, not lifetime by itself. The old child remains framework-owned until cleanup actually completes.

## Validation map

The contract is covered by the existing subsystem tests plus the shared-handle characterization tests:

- `GenerationRetirementTests` proves caller cancellation and bounded waiting do not cancel framework-owned cleanup.
- `DynamicModuleTests` and `DynamicRollbackTests` cover post-retire lease rejection, exact drain boundaries, forced cancellation, deferred release, and client/server cleanup behavior.
- `SharpLinkMultiClusterClientTests` covers replacement rollback, post-publish caller cancellation, coordinator-owned retired cleanup, cleanup failure, and replacement/stop races.
- `DynamicEndpointIntegrationTests` covers resolver snapshot/generation replacement, endpoint removal, and in-flight work during topology changes.
- `StaticEndpointIntegrationTests` covers GoAway removing a draining connection from new selection while existing work drains.
- `RuntimeAssemblyIntegrationTests` covers collectible dynamic assembly generations and final ALC release.

Because the shared retirement handle is used only on lifecycle/control paths, it adds no request-path lock, allocation, lookup, or selection branch.
