# Physical ownership integration with current dev

This integrates the #931 source at `5d33282d65958a41db3d019702b7cca56270b352`
with the current dev lifecycle fixes. It preserves the original PR history.
Historical #931 green jobs and performance samples do not certify this combined
tree. The final integrated commit still needs its own validation and review.

## Production boundary

The physical-ownership change remains the same six production files: the failure
association/owner helper, Client interceptor cleanup, dynamic module handoff,
retry terminal aliases, generated Server stream cleanup, and Server exception
mapping/invocation retention. No additional ordinary-RPC maintenance predicate,
catalog lookup, epoch, counter, or registration is introduced by the integration.
Exceptional owners and graph operations allocate; changed exception handling and
native layouts are not a performance-equivalence claim.

The combined source retains #930 queued registration identity, #932 intrinsic
backpressured-send ownership, #935 close-before-drain, #937 packed assembly
identity evidence, and #940 instance-specific dispatcher retention assertions.
The duplicate retry-timer fixture from #932 is not added a second time.

The public terminal and physical completion are distinct. An outstanding user
operation completes before its enumerator disposal begins; disposal and linked
source finalization complete before the transferred module owners are released.
The Server invocation retains its existing request/service/module ownership
after publishing the selected terminal. This is not a complete static-client
Stop proof, cancellation-callback census, or the #933 maintenance protocol.
Uncooperative user work can retain ownership indefinitely.

Fresh cancellation carriers preserve the standard OCE/TCE category, message and
token; SharpLink carriers preserve code/detail/message. They do not clone outer
exception identity, custom subclasses, arbitrary metadata or a TCE's `Task`.
The original remains the inner cause. Explicit carrier metadata gives the Server
mapper the original logical exception even after cleanup; retry replacement
terminals remain distinct logical outcomes. Arbitrary application wrappers are
not unwrapped.

## Explicit test-fixture changes

These inherited #931 changes are separate from the production lifetime fix:

| Fixture boundary | Before | Integrated contract |
| --- | --- | --- |
| Queued reader whole 11,000-operation workload | 10 s | 30 s |
| Queued reader method-level parent ceiling | 30 s | 60 s |
| Each queued callback and dedicated-worker join | 10 s | 10 s, unchanged |
| Reader allocation child, including startup and priming | 30 s | 30 s, unchanged |
| Allocation parent launch/cleanup/reporting ceiling | 30 s | 90 s |

The original 11,000-operation exactly-once workload, per-callback failure,
worker-exit witnesses and parallelism remain. The delayed-progress and genuinely
withheld-callback controls distinguish a total workload overrun from a lost
callback. See [the fixture report](reader-dispatch-fixture-lifecycle.md); it would
be incorrect to describe every timeout as unchanged.

The allocation merge retains **both** independent conditions: the child's
read-only-verified stable, warmed cast cache and dev's #935 Gen2-disturbance
rejection. Each consumer still runs once, with 1,000 warmups, 10,000 measurements
and an exact zero-byte assertion. No allocation is subtracted, no failed window
is retried until zero, and no test is serialized to obtain a pass. Historical
method-body hashes predate the additional Gen2 guard and are identified as such
in [the oracle report](../docs/validation/read-ownership-allocation-oracle.md).

## Added composition controls

- `CancellationCarrierPreservesLogicalMapperInstanceAndDeclaredMetadata` uses a
  genuine non-null `TaskCanceledException.Task` and a custom OCE subclass. It
  checks the fresh outer carrier, retained original cause, category/message/token,
  intentionally un-cloned `Task`, exact mapper instance and sequential disposal.
- `SessionShutdownMustNotCompleteAbandonedProducerDisposal` puts the user
  `MoveNextAsync` inside a real intrinsic send-capacity wait. Both cancellation
  and deadline are selected while it waits. Send cleanup and session shutdown
  finish independently; the producer's held disposal and linked source remain
  owned until their own physical completion. Generated StreamData itself uses
  nonwaiting `SendPacket` and is not mislabeled as this capacity-waiting API.
- `RetiringQueuedSelectionMustNotReleaseAdmittedPhysicalCleanupOrRetarget` uses
  real TCP and a one-permit admission program. One old invocation retains a
  physical tail while another R1 request waits. Zero-grace replacement can cancel
  the queued wait immediately with `Unavailable`; that must neither invoke R2
  nor release the active invocation's permit, service or module. Both deadline
  and module-retirement terminals are exercised, then fresh traffic reaches R2.

The initial authored capacity fixture failed because it had not reached the
intended waiting API. The initial queued fixture incorrectly required the queue
to remain pending after zero-grace retirement; retained diagnostics showed
`permits=1`, `calls=1`, `streams=1`, `serviceDisposed=0`, but a legally rejected
queued task. Those fixture failures are retained separately and are **not**
presented as product counterexamples.

Four temporary product counterfactuals were rejected by the added controls:
losing the cancellation token, passing a carrier to the mapper, omitting linked
source disposal, and releasing the Server invocation before its physical tail.
All altered production bytes were restored and normal test binaries rebuilt.

## Inventory and validation status

An initial combined-source local run executed 2,282 unit cases with zero
failures/skips/cancellations. Comparing actual report case identities with dev's
2,169 cases found 113 additions and no removals. A separate complete diagnostic
run executed 2,290 cases: the same 2,282 plus exactly the eight unchanged lease
controls. These observed inventories calibrate the four historical collectors;
their focused and old-negative expectations are unchanged.

On local Arm64/.NET 10.0.11, the combined reader lifetime filter passed all 37
cases. Both child reports proved one measurement, the actual module/runtime/
architecture, the unchanged cache table, flowing non-default context and 0 bytes.
The genuine `new byte[256]` control failed at exactly 280 bytes, then the restored
source passed. These are local combined-source results, not a claim about an
unexecuted final-head or another architecture.

Local reports, case-set differences, failed authored fixtures and product
counterfactual logs are retained under `artifacts/931-integration/` in the authoring
checkout. That ignored directory is not a published artifact or a substitute for
fresh CI uploads. Final committed-head historical runs, full correctness,
API/package/trim/AOT and matched performance evidence must be recorded separately.
