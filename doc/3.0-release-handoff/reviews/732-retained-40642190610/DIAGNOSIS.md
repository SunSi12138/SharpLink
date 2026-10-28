# #732 retained-evidence diagnosis — 2026-10-28

## Decision

Keep **M0 NoGo / M1 Hold**. No unchanged timing rerun; no F2 product change. Prepare independently reviewed **two-process A-only non-timed phase/runtime diagnostic**. Historical run 40642190610 remains valid historical evidence, complete, and inconclusive. This report did not run a build, test, RPC, or performance workload.

## Inputs and independent calculation

Read HANDOFF plus technical-decision-tree, method-facts-variance, status, cloud-resources at `ba99c6f9467fd85233afbc0dc1c84cf839e1b5a0`; frozen CONTROLLED-PLAN at `47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f`; retained archive `732-hosted-three-arm-36.tar.gz` from evidence commit `5a6cf6c490b43c083fb772e2c6f2663fa94bbe16`. O=`a6bd0352a931d5c2816e42778055e90efb01144b`, A=`37548b7a35d50803212d2b0a637306636cc0a1d3`, B=`2756e8810772cf956ee70ed380925ecc86e86a32`; B production change is `652ffca720ba188a7e544f1f5e2da7dafa5e1568` (no src diff to B).

`analyze_retained.py` independently validates all 36 process files, ordered 720 rows, 2,000 completed calls and ticks per row, no failure, and 1,440,000 calls. Recomputes whole-window mean, pooled nearest-rank P99, oriented cross-pair logs, contrast-specific chronological same-arm noise and every frozen decision; full-precision summary in `retained-analysis.json`. The recalculated six conclusions all exactly match **inconclusive**.

| Primary I/T contrast | Mean GM | P99 GM | Adverse P99 pair indices (zero-based) |
|---|---:|---:|---|
| B/O | 1.0191221303303852 | 1.0537540200965323 | 4,8 |
| B/A | 1.0109137301000377 | 1.0330544858565338 | 2,6,11 |
| A/O | 1.001740972436624 | 1.0148279182189728 | 1,10,16 |

Recorded preparation has six builds, 12 semantic preflight requests, clean exact inputs, private 10.0.11 runtime and CPUs 0,2 (distinct VM-reported cores; not dedicated physical-host cores). All O/A/B timed preflight rows have request/state IDs and service/context invocation tokens matching, distinct from connection token. All untimed synchronous rows have no matching state and use connection token in context even though context.CanBeCanceled is true. These are real semantic preflights, not JIT-stage evidence.

## New discriminating observation: drift predates M0

| Cell / arm | First slower than fifth | Median per-process first/fifth mean |
|---|---:|---:|
| P/T O | 12/12 | 1.4215558479 |
| P/T A | 11/12 | 1.3435377749 |
| P/T B | 12/12 | 1.3292531449 |
| I/U O | 11/12 | 1.0849575198 |
| I/U A | 11/12 | 1.1160508073 |
| I/U B | 11/12 | 1.1880094877 |

No I/U sample has a recorded collection. Thus neither M0 projection nor M1 caching is necessary for these trajectories. This does not prove product neutrality: it identifies an already-present phase/process confound that must be measured before another comparison.

Reproduced appendix: P/T all-arm median chunk means 117.87,122.14,105.67,95.58,87.97 us; I/U 83.59,78.93,75.06,75.27,74.47 us. Samples 117.771–391.691 ms, median 172.396 ms. Same-arm CPU and central-latency movement rules out a description solely in terms of rare tail outliers. No attribution to GC duration or scheduling follows from collection counts. Fixed order means cell shape and process age remain confounded; shared generated/runtime methods can tier across successive cells even though server/client objects are recreated. Do not delete chunk 1 or substitute chunk 5.

## Actual source mechanism and F2 boundary

Source read at exact O/A/B includes `RpcMethodFacts.cs`, `SharpLinkInterceptors.cs`, `SharpLinkServer.cs:171-240`, InvocationDispatch, AdmissionDispatch, Interceptors, DispatchSelection, and `RpcGenerator.StubEmitter.cs:67-134,238-256`.

- A resolves once from original registration and validates known/unknown/invalid plus contract/method IDs. Generated static facts cover all five RPC shapes and feed invocation; queued work retains scalar facts + weak generation selection rather than rebinding current registration. B changes only facts representation, not those semantics.
- A's ToDescriptor unpacks scalar flags/timeout and constructs a value descriptor; B returns its cached immutable descriptor after the same known check. Facts are static per method, not allocated per call; cold 48→56-byte facts size is not +8 B/RPC.
- Server context already stored a descriptor in O. O→A replaces `long InterceptorMethodId` with a facts reference and calls ToDescriptor at construction. Hence duplicate descriptor/facts semantics exist, but source alone does not prove a new 32 B/op cost or locate the historical regression.
- Retained I/U allocation medians O/A/B are 1128.3852 / 1128.3688 / 1128.3188 B/call. They neither measure exact context size nor reveal a new whole-context allocation penalty. There is no actual TCP native body demonstrating expensive context copies or an interceptor-specific causal signal.
- Therefore F2 prerequisites are unmet. A future F2 proposal needs actual context constructor/caller code, measured layout/allocation/copy evidence, and interceptor-specific signal, while preserving constructor-time unknown rejection and descriptor-copy observer semantics. Do not implement it now.

## Evidence gap, not invented attribution

Inventoried all retained #732 hosted, M0-original and M1-local archives: no .nettrace/.etl/perf/code-version/kernel context-switch trace or corresponding event-aligned table. Hosted evidence has five-second process/load/PSI/cgroup snapshots only, much coarser than chunks; no warmup/chunk absolute start markers. There are no actual JIT tier events with which to identify transitions and no per-thread scheduling events. Root cause remains unresolved among runtime transitions, GC pauses, managed queueing and runnable/off-CPU effects. Absence of a standalone ToDescriptor body could mean inlining, not zero cost.

## Next finite step

Tool-only diagnostic: two fresh A processes on private 10.0.11 default tiering/PGO/workstation GC, same two-distinct-reported-core policy and unchanged original service. Keep I/T,P/T,I/U,P/U order; each 1,000 warmup + five 2,000-call chunks; 88,000 total invocations across two processes. No call latency arrays, throughput, acceptance ratios or new thresholds. Emit phase markers outside service/product code, trace from runtime startup, retain exact executable/dependency/tool/runtime identities and outputs. Stop after the two attempts; failure preserves partial evidence, no replacement.

Collect CLR JIT/method-load/code-version, GC start/stop/suspend and ThreadPool events plus phase provider; map actual generated Facts proxy/stub and dispatch/stream pump including inlined caller context. Event-aligned output must contain coverage/loss counts, method versions/tier/time/phase, GC suspension intervals, threadpool events, and scheduler availability.

Kernel capture is optional only if already capable and allowed, no sudo/escalation/sysctl/permission changes. Official docs distinguish collect/EventPipe from collect-linux: Linux >=6.4, CONFIG_USER_EVENTS, tracefs and root/capabilities are prerequisites; thread-time emits actual context switches. Missing prerequisites => explicit scheduler-unavailable, never label sampled stacks as scheduler attribution. See https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace . Record installed version/help and use supported syntax, not assumed profile names.

Traces can support mechanistic hypotheses, not quantify how much old performance was caused by JIT nor clear NoGo. After event-aligned diagnosis, independently approve any revised warmup/order campaign. All five RPC facts-once, original generation, full ticks/nullable timeout, 0–127 streams, cancellation/payload/nullability/idempotency, ABI/API5 complete identity/wire2.4, CTS/ALC and no hot-path management remain fixed. User +10% QPS/-20% P99 vs2.0.3 is a stretch target, not acceptance relaxation.
