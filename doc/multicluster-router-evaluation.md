# MultiCluster router simplification evaluation

Issue: #405

## Decision

**No-Go: retain the current `SharpLinkMultiClusterClient` coordinator.**

The proposed `contract assembly -> ISharpLinkClient` router is a useful description of the MultiCluster **data-plane boundary**, but it is not a replacement for the current coordinator.

The current implementation already routes exactly once when a proxy is acquired. `Get<TContract>()` reads one immutable route snapshot, resolves the owning child, and returns that child's cached generated proxy. Subsequent RPCs execute directly through the selected child. There is no MultiCluster lookup, lock, allocation, or extra slot hop on the cached RPC path.

A benchmark-only assembly router can make the acquisition lookup somewhat cheaper in some cases, but it does not own startup rollback, dynamic assembly registration, runtime slot mutation, transition budgets, old-child retirement, or coordinated shutdown. Moving those responsibilities to application/DI/Hosting code, or introducing a separate lightweight lifecycle owner, recreates the current coordinator outside the current type without reducing system complexity.

No production runtime change is recommended by this evaluation.

## Current responsibility boundary

The existing design has two distinct layers that should not be conflated.

| Responsibility | Current owner | Router-only replacement |
| --- | --- | --- |
| Physical endpoint resolution, connection/pool state, load balancing, retry/circuit breaker, per-child interceptors | ordinary `ISharpLinkClient` child | unchanged |
| Static contract-to-child selection at proxy acquisition | MultiCluster immutable route snapshot | assembly router can do this |
| Cached proxy / RPC execution | selected child client | unchanged; router is absent |
| Bounded parallel startup of all required children | MultiCluster coordinator | must move elsewhere |
| Partial startup failure rollback and cleanup | MultiCluster coordinator | must move elsewhere |
| Aggregate configured-connection budget | MultiCluster coordinator | must move elsewhere |
| Replacement transition budget while old/new children coexist | MultiCluster coordinator | must move elsewhere |
| Runtime add / replace / remove transaction serialization | MultiCluster coordinator | must move elsewhere |
| Prepare/connect candidate before atomic publication | MultiCluster coordinator | must move elsewhere |
| Preserve old cached proxy binding while retiring old child | MultiCluster coordinator + ordinary child | must move elsewhere |
| Dynamic assembly ownership by cluster | MultiCluster coordinator | assembly router alone is insufficient |
| Dynamic registration migration during slot replacement | MultiCluster coordinator | must move elsewhere |
| Dynamic unregister/replacement serialization and rollback | MultiCluster coordinator | must move elsewhere |
| Retired child cleanup and framework task observation | MultiCluster coordinator | must move elsewhere |
| Aggregate health / cluster-specific health | MultiCluster coordinator delegates to children | must move elsewhere or API regresses |
| Hosting startup publication only after successful aggregate connect | Hosting + MultiCluster coordinator | Hosting would need to recreate orchestration |
| Coordinated stop/dispose across all children | MultiCluster coordinator | must move elsewhere |

This ownership split is intentional: ordinary clients own the internals of each connection topology; MultiCluster owns the **set of ordinary clients** and the transactions that change that set.

## Candidate models

### A. Application or DI owns independent clients; MultiCluster becomes a pure router

Conceptually:

```text
contract assembly -> ISharpLinkClient
```

This is attractive for steady-state lookup, but the application or Hosting layer would then need to implement aggregate startup, rollback after partial connect, total connection budgets, replacement overlap budgets, runtime slot transactions, dynamic registration ownership/migration, old-child retirement, aggregate health, and coordinated shutdown.

That is a No-Go under #405: the state machine did not disappear; it moved to a less appropriate layer and made the application-facing model more complex.

### B. Pure router plus a new lightweight lifecycle owner

This keeps the router small but introduces another object that owns the exact responsibilities listed above. Once that owner supports the required add/replace/remove, rollback, budget, drain, dynamic-registration, health, and shutdown semantics, it is functionally the current coordinator split across two types.

That is also a No-Go: it increases indirection and ownership ambiguity without removing the required state machine.

### C. Retain the coordinator and keep routing acquisition-only

This is the current model. The coordinator owns control-plane transactions; the selected ordinary child owns the RPC path. This preserves the desired performance boundary while keeping lifecycle ownership explicit.

This is the selected model.

## Behavioral comparison against #405

### Startup and partial failure

The coordinator materializes ordinary child clients, connects required slots with a bounded parallelism policy, and rolls back already-started children if aggregate startup fails. A pure route table cannot provide this behavior by itself.

### Multiple contract assemblies

Static routing is generated/configured ahead of use and published as an immutable snapshot. The route is resolved at `Get<TContract>()`; no route selection occurs during the subsequent RPC.

### Cached proxy behavior

Repeated `Get<TContract>()` calls return the selected child's cached proxy for the published generation. Existing unit coverage verifies reference stability and allocation-free repeated acquisition. Runtime replacement coverage verifies that a newly acquired proxy uses the replacement child while an already-acquired proxy remains bound to the old child until that child retires.

### Runtime add / replace / remove

Mutation preparation happens outside the publication gate. Publication is atomic. Replacement keeps the old child alive while existing proxies drain, and removal prevents new acquisition while preserving already-bound work. The coordinator also serializes conflicting runtime assembly lifecycle operations with slot mutations.

### Dynamic assembly registration

The coordinator records which cluster owns a dynamically registered contract assembly and delegates the actual generated artifact registration to the selected ordinary child. Replacement migrates those registrations before publishing the replacement slot. A pure assembly route table cannot replace that ownership transaction.

### Child drain and disposal

Retired children are observed and disposed by coordinator-owned cleanup tasks. Caller cancellation after publication does not roll back published state; cleanup continues under framework ownership.

### Health, Hosting, and shutdown

Cluster-specific health delegates to the chosen child. Hosting starts one coordinator and publishes it only after aggregate connect succeeds. Shutdown coordinates all children and observes cleanup failures. Removing coordinator ownership would require Hosting to reproduce the same aggregate lifecycle.

### Budget enforcement

MultiCluster currently owns both a steady-state aggregate connection budget and a bounded transition budget for replacement overlap. These are properties of the set of children, not of an individual ordinary client or a route lookup.

## Performance experiment

The evaluation includes a benchmark-only prototype in `MultiClusterRouterEvaluationEvidenceRunner`. The prototype owns only:

```text
Assembly -> ISharpLinkClient
Get<T>() -> child.Get<T>()
```

It intentionally owns no lifecycle state. The evidence run used the same branch head and one GitHub-hosted runner for all variants.

Environment:

- evidence head: `4c417d40a0c69b43471f2ce1ba7de7aa596076db`
- .NET 10.0.11
- Ubuntu 24.04.4 LTS, x64
- 4 logical processors visible to the process
- acquisition concurrency: 1 / 8 / 32 / 128
- RPC concurrency: 1 / 8 / 32 / 128
- primary RPC transport: TCP loopback
- control transport: SharedMemory

The temporary workflow used to collect this evidence was removed after the run; the runner remains reproducible through:

```bash
dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
  -c Release -- --multicluster-router-evidence <output-json>
```

### Proxy acquisition

Each row below uses 524,288 acquisitions. All three variants measured **0 steady-state managed B/op and 0 managed objects/op** after warmup.

| Concurrency | Direct client ns/Get | Current MultiCluster ns/Get | Assembly router ns/Get | Current M Get/s | Router M Get/s |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 61.65 | 72.68 | 67.19 | 13.76 | 14.88 |
| 8 | 23.40 | 46.52 | 33.95 | 21.50 | 29.45 |
| 32 | 23.04 | 38.43 | 37.83 | 26.02 | 26.43 |
| 128 | 27.09 | 38.98 | 43.35 | 25.66 | 23.07 |

The assembly prototype is about 7.6% cheaper than current MultiCluster at concurrency 1 and 27.0% cheaper at concurrency 8, essentially tied at 32, and about 11.2% slower at 128. The absolute acquisition cost remains tens of nanoseconds and allocation-free.

Routing lookup count is the important structural result:

| Variant | Lookups per `Get<T>()` | Lookups per cached RPC |
| --- | ---: | ---: |
| Direct child | 0 | 0 |
| Current MultiCluster | 1 | 0 |
| Assembly router prototype | 1 | 0 |

The prototype does not improve the cached RPC path because there is no coordinator lookup there to remove.

### Cached RPC path

The prototype returns the **same proxy object** as the direct child (`ReferenceEquals == true`). Therefore direct-versus-prototype throughput/latency deltas in this short end-to-end run are measurement-order/JIT/server-scheduling noise, not different RPC code paths. The useful comparison is that current MultiCluster also performs zero route lookups once its proxy has been acquired and remains in the same general transport envelope.

TCP loopback evidence:

| Concurrency | Variant | Requests/s | P50 us | P99 us | Managed B/op |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | direct | 11,390 | 83.59 | 135.85 | 808.11 |
| 1 | prototype, same direct proxy | 13,366 | 72.79 | 111.29 | 808.01 |
| 1 | current MultiCluster | 15,978 | 61.11 | 93.45 | 807.71 |
| 8 | direct | 53,181 | 145.84 | 250.84 | 386.53 |
| 8 | prototype, same direct proxy | 57,236 | 134.87 | 249.61 | 382.95 |
| 8 | current MultiCluster | 68,447 | 108.45 | 219.24 | 407.75 |
| 32 | direct | 103,187 | 297.31 | 541.57 | 333.99 |
| 32 | prototype, same direct proxy | 87,378 | 334.54 | 967.47 | 331.00 |
| 32 | current MultiCluster | 184,943 | 160.90 | 342.39 | 334.78 |
| 128 | direct | 131,095 | 919.36 | 2,010.99 | 352.84 |
| 128 | prototype, same direct proxy | 149,137 | 816.60 | 1,450.71 | 318.64 |
| 128 | current MultiCluster | 450,434 | 277.33 | 415.56 | 339.04 |

SharedMemory control evidence:

| Concurrency | Variant | Requests/s | P50 us | P99 us | Managed B/op |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | direct | 17,103 | 56.68 | 90.46 | 1,255.72 |
| 1 | prototype, same direct proxy | 19,379 | 52.45 | 80.46 | 1,246.75 |
| 1 | current MultiCluster | 19,107 | 51.25 | 79.94 | 1,242.56 |
| 8 | direct | 155,555 | 50.69 | 75.24 | 430.00 |
| 8 | prototype, same direct proxy | 103,953 | 54.26 | 717.01 | 432.95 |
| 8 | current MultiCluster | 167,965 | 46.95 | 74.50 | 434.71 |
| 32 | direct | 378,739 | 68.36 | 182.90 | 356.59 |
| 32 | prototype, same direct proxy | 474,083 | 64.17 | 123.75 | 343.34 |
| 32 | current MultiCluster | 430,105 | 72.42 | 128.84 | 353.25 |
| 128 | direct | 708,589 | 169.29 | 329.07 | 341.42 |
| 128 | prototype, same direct proxy | 660,313 | 180.57 | 364.89 | 328.38 |
| 128 | current MultiCluster | 697,256 | 174.66 | 331.72 | 329.00 |

The exact cross-thread object count for asynchronous RPC work is not exposed by the in-process GC allocation API used here, so the runner reports total managed bytes/op and leaves objects/op unset for this portion. This does not obscure router overhead: acquisition reports exact zero allocations, and cached RPC performs zero router lookups by construction.

### Immutable route publication

A synthetic one-entry snapshot publication isolates only route-map preparation/publication cost:

| Route key | Mean ns/publication | Managed B/publication |
| --- | ---: | ---: |
| current type-key prototype of the existing route map | 791.54 | 368.13 |
| assembly-key prototype | 300.97 | 368.13 |

The assembly-key map is about 62% cheaper in this isolated one-entry test. This is a control-plane micro-cost. It deliberately excludes candidate child construction/connect, dynamic registration migration, budget validation, old-slot retirement, drain, rollback, and cleanup. Those required semantics dominate the real mutation ownership question and still need an owner.

## Go / No-Go criteria

| #405 criterion | Result |
| --- | --- |
| Route selection only at proxy acquisition | Already true in current design |
| Cached RPC has no coordinator/router lookup | Already true; measured/structurally verified as 0 |
| Cached RPC has no new router allocation/synchronization | Already true |
| Proxy stays bound to selected child/generation | Already true and covered by replacement tests |
| Simpler route lookup | Prototype is somewhat cheaper in some acquisition cases, not consistently across concurrency |
| Preserve startup rollback | Requires coordinator-equivalent owner |
| Preserve runtime add/replace/remove transactions | Requires coordinator-equivalent owner |
| Preserve dynamic registration ownership/migration | Requires coordinator-equivalent owner |
| Preserve aggregate/transition budgets | Requires coordinator-equivalent owner |
| Preserve retired-child drain/disposal | Requires coordinator-equivalent owner |
| Preserve Hosting lifecycle without duplicating orchestration | Requires coordinator-equivalent owner |
| Avoid moving complexity to application/DI/Hosting | Pure-router design fails this criterion |

The proposed redesign therefore does not satisfy the issue's Go condition. Its only clear simplification is the lookup data structure, while the stateful control-plane owner remains necessary. Recreating that owner elsewhere would be a rename/split, not a reduction in system complexity.

## Result

Retain `SharpLinkMultiClusterClient` as the owner of cross-child control-plane state and transactions. Continue treating its immutable routing snapshot as an acquisition-only router and keep ordinary child clients authoritative for physical connectivity and the RPC execution path.

If a future issue removes or materially narrows runtime slot mutation, dynamic registration migration, aggregate budgets, or coordinated Hosting lifecycle, this decision can be revisited because the coordinator's required ownership surface would then be smaller.

After this evaluation record is merged, #405 should be closed as **not planned** rather than treated as an incomplete production refactor.
