# R: readonly inner retained-send lease candidate

## Scope and provenance

R starts from exact G2 tree `398d484fb5b8ab8adb75a74db7d577d929d2dc77`. The production patch has only three one-line changes: the synchronous `SendUnsizedStreamChunkResolvedAsync<T>` lease parameter becomes `in`, and its two production dispatch-wrapper callsites explicitly pass `in creditLease`.

The rejected A (first-binding awaiter) and V (send-validator) changes are excluded. No other production code, existing test, benchmark, configuration, allocation gate, protocol rule, timing workload, or lifecycle check is changed. The new test overlay is applied identically to original G2 and R; it is not part of either production codegen source tree.

## Ownership and reentrancy

Both `SendClientStreamChunkResolvedAsync<T>` and `SendGeneratedStreamChunkResolvedAsync<T>` retain by-value lease parameters. Their owned owner/state/generation/request/stream snapshot exists before client deadline/time-provider reads, either wrapper's size probe, and the inner serializer. Passing a readonly reference to that already-owned snapshot does not expose the mutable caller's lease variable or producer state-machine field.

The lease is a readonly struct. It does not make its referenced pooled state immutable. Original owner/type/attachment/generation checks remain authoritative, including same-key and different-key pooled reuse during user callbacks. There is no re-resolution, generation refresh, changed rejection order, or extra cancellation/deadline guard.

The inner unsized method remains synchronous. It uses the reference only while its wrapper-owned snapshot is live. Before returning a pending ValueTask, it calls `AwaitPreCreditBudgetAndRetainedFlowCreditAsync` with a by-value lease, so the async state machine owns its snapshot before its first suspension. A pending serialized-budget acquisition is specifically tested, rather than relying only on a flow wait that has already captured controller identity.

`SendStreamChunkKnownSizeResolvedAsync`, the retained async helper, and `ReturnUnsentStreamCredit` remain by value. Writer/budget transfer order and catch/finally behavior are unchanged. Existing cold slow-path and refund copies are intentional ownership boundaries, outside this candidate.

## Same-overlay controls

The standalone `ResolvedStreamLeaseOwnershipTests` adds 14 cases; all original 2,128 cases remain byte-identical. Expected full suite with overlay: 2,142 cases, zero skipped, separately for G2 and R. Default and experimental source configurations must keep the same expectations.

- Three direct client-wrapper alias controls replace an actual mutable caller `holder.Lease` field during serialization, the outer deadline clock callback, and a size probe that falls back to unsized serialization. No by-value test forwarding helper masks the snapshot boundary. Both original and unrelated state credit, plus emitted request/stream keys, are asserted.
- Four lifecycle controls cover client and generated second-item paths and both same-key and different-key pooled-state reuse. The callback proves physical state identity is reused with a new generation; the owned stale generation must reject admission without debiting/refunding its replacement.
- Four post-admission clock controls cover client and real generated retained paths, deadline expiry and cancellation thrown by user clock code. Each callback first asserts both individual state credits and connection credit, then replaces caller storage. This prevents a wrong admission and wrong refund from merely cancelling in the aggregate. The unrelated stream's prior debit is preserved; after returning any published first-item credit, a duplicate retained refund must be rejected. Callback-thrown cancellation is not described as a new production cancellation recheck after GetTimestamp; ordinary cancellation is also covered by the unchanged suite.
- Two slow-capture controls force the serialized-budget acquisition itself to be pending before the synchronous publication call returns. Generated first DATA is published before the iterator installs the blocker between item one and item two; it cannot silently test first-binding suspension. Caller storage is replaced and the same wrapper/inner stack is re-entered with an intentional serializer exception before admission. This respects global waiter fairness instead of awaiting a supposedly fast unrelated send behind the flow waiter. Budget and original flow credit are then granted separately. Per-state credit, no budget owners/waiters, and wire keys prove original ownership.
- One signature control requires both dispatch wrappers, the sized sender, the retained async helper, and the refund boundary to retain by-value lease parameters. It intentionally allows the inner method's baseline value or candidate readonly-reference signature so the identical overlay passes on both sources.

No test assumes zero timers or a particular timer count; the existing deadline scheduler may own a disarmed timer. No generated state-machine fields are modified by reflection. Reflection only observes established session/controller state and parameter signatures for assertions.

## Verification boundary

Local checks are Python/git/source-only. No C# build, tests, codegen workload, timing, push, publication, or CI dispatch was executed for this candidate here. The repository's project-reference Python guard delegates to dotnet and could not run because dotnet is absent; maintainability, compiler/analyzer, and formatter gates likewise remain for GitHub.

Before accepting R: run exact same-overlay full/focused safety and unchanged allocation/ownership gates; compile with zero warnings; verify formatting and maintainability; then inspect actual retained-route JIT and NativeAOT callers. The target is the second 40-byte lease copy between wrapper snapshot and inner call. Confirm its removal and audit compensating copies/spills in caller and callee, while preserving the original snapshot and deferred ownership copies. Record the actual tier/PGO source and distinguish lease copies from the separate RpcDeadline value. This source patch makes no performance claim. Any timing comparison and Ready for review decision requires later real evidence and the parent's existing acceptance gates.
