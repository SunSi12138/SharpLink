# ADR 0002: Intrinsic ownership of capacity-waiting sends

- Status: Proposed
- Date: 2026-10-22
- Related: `BackpressuredSendOwnershipTests`, `SendPumpTests`, `SendPumpProgressIsolationTests`

## Context

A send waiting for byte capacity is not yet in either send FIFO or `QueuedSendBytes`.
Some server early rejection responses have already released their admission/request
ownership when that wait escapes the reader's synchronous dispatch prefix. Therefore
neither zero queued bytes nor an ordinary FIFO flush marker proves that these older
response producers have finished. A smaller later normal frame can enter FIFO first.

The existing broadcast capacity signal also has a first-waiter missed-pulse window:
its final failed reservation check precedes signal publication, whereas byte release
may skip the admission lock when the signal reference is null. A release in that gap
can leave a connected, idle pump with zero queued bytes and a stranded waiter.

Adding a maintenance-specific count/lock/registry to every RPC, including saturated
RPCs, is not the selected solution. Even a queue-full-only ledger changes the normal
saturated cost, and growing a pump can change native fast-path field accesses.

## Decision

Replace the broadcast signal with intrinsic pending-enqueue operations. The existing
cold pump-task reference is a task-or-queue lifetime union: an actual capacity wait
places the original task in the queue, then the final physical cleanup restores the
task. The existing capacity-signal reference remains only a wake slot for real waiters. A pending node owns its frame, cancellation registration and
final enqueue completion. There is no second maintenance counter or registry.

- Publish a queue reference, or an immutable registration marker, before the final
  capacity **and stop** rechecks under the existing admission gate. A release/stop that skipped a null reference must be
  observed by those rechecks; one that sees the publication joins the gate.
- A pending frame moves into the actual normal/progress FIFO under that gate before
  its producer is completed. The normal and protocol-progress budget rules remain
  unchanged. Waiters are scanned in arrival order, but unavailable larger frames do
  not block smaller/progress frames. Fast nonwaiting producers can still race them;
  this is not a global fairness guarantee.
- The one doubly-linked ownership list has a waiting prefix and retiring suffix.
  A node moves to the suffix using its existing links when its capacity work ends.
  Pulses traverse only the waiting prefix; when it empties the wake slot becomes
  null even if physical cleanup is held. Unrelated successful releases therefore
  do not acquire a gate or scan cleanup-only nodes.
- Canceled or rejected nodes remain visible until their writer has been returned,
  including a throwing return. Cancellation-registration disposal and writer return
  never run under the admission gate. Failed cleanup is executed asynchronously,
  and the node's ordinary completion represents its termination.
- An accepted node no longer owns its frame: the FIFO/pump owns its subsequent flush
  and return. Its cancellation registration is disposed before its completion.
- `WaitForStopAsync` joins pending-node cleanup after the pump terminates. Stop seals
  further pending-node admission. A held cleanup cannot be silently abandoned by
  session disposal.
- The empty `PulseCapacityWaiters` path retains the existing null check; the nonempty
  algorithm is outlined so its locals do not introduce stack zeroing on every normal
  packet release.

`CaptureBackpressuredSendDrain` is an internal cold snapshot of intrinsic pending
operations, not a producer seal and not a transport-flush certificate. Its caller must
first seal ingress and physically drain all ordinary and control producer parents,
including terminal callbacks and any admission-to-send handoff. After this join the
caller must enqueue/observe its normal-FIFO reverse fence. Failures are observed by
the send caller; the cold join signifies physical cleanup, not successful delivery.
No maintenance protocol, public maintenance API, catalog reclamation guarantee or
arbitrary future-producer closure is introduced by this prerequisite.

## Consequences

Immediate successful send source and frame/session/pump field counts do not gain a
maintenance read, branch, counter, copy or lookup. This is not alone a performance
proof: native code of both admission and ordinary release must also be inspected.

Capacity waiters now have per-operation nodes instead of sharing broadcast epochs.
The redundant async enqueue wrapper and separate completion-source object are removed
to offset that cost; the new node itself supplies its task. A Full attempt that finds
capacity at its final recheck allocates no temporary queue. The immutable marker is
allocated once at type initialization, holds no nodes or session, and never joins a
cross-session census. A genuinely blocked epoch allocates one queue, including the
original pump task and waiting-prefix boundary. Cancellation
has a per-node registration and failed cleanup uses a work item. Their costs must be
reported separately from uncongested sends; speedup is not inferred from a noisy
non-significant result or from excluding failed baseline runs.

Disposal now waits for pending cleanup and can remain pending if a custom writer
blocks forever. That is physical ownership, not a timeout success. The caller can
cancel its wait without proving that cleanup has stopped.

## Alternatives considered

- Retain admission/module/request owners longer: useful where they already exist,
  but disabled-admission and early unimplemented/deadline/capacity errors can lack
  such an owner. It does not cover the general session pending-enqueue operation.
- Snapshot queued bytes, the old broadcast task, or a FIFO flush: none owns the
  interval between wake/reservation and actual enqueue/return.
- Keep retiring nodes in the capacity-wake slot: rejected. A held custom return
  makes later ordinary releases acquire a gate and scan completing nodes even when
  no capacity waiter remains. Retaining them in the existing cold lifetime slot
  removes this mixed-state tax without adding a separate node or registry.
- Add a Full-branch ledger: rejected. It adds control-specific lock/count work on
  saturated ordinary calls and grows/relocates the pump fields.
- A process-wide task/weak registry: rejected. The lifetime and failure domain must
  remain the exact session, and ordinary calls must not join a global census.

## Validation

Deterministic tests demonstrate the old FIFO overtaking and false-empty schedules,
then verify the conditional cold drain, all early error codes, late capacity,
synchronous cancellation, cancellation/grant races, head cancellation, throwing or
blocked return, terminal cleanup, disposal and unrelated-session isolation.
Existing send-pump/progress/lifecycle tests check protocol and queue behavior.

Synchronous queue-byte telemetry is a reentrancy boundary even while the admission
gate is held. A waiting node claims completion before reserving capacity, so a
listener cannot cancel or stop the same node into a second cleanup owner. A failed
reservation emits no telemetry and restores the waiting state. After a successful
reservation, traversal reads the claimed node's current successor: listeners may
cancel siblings or append waiters. This preserves a single forward scan. The final
reservation recheck also reloads the lifetime queue before updating the wake slot,
so a listener's newly published waiter remains wakeable.

Four regression cases cover cancellation of the granted node, Stop with a held
writer return, sibling cancellation plus an appended waiter, and publication during
the final reservation recheck. The original source fails both granted-node controls;
the intermediate claim-only repair still fails the final-recheck wake-slot control.
The repaired source passes all 26 ownership fixture cases in a direct local replay
against the real runtime. That replay is not a TUnit run; exact-head hosted TUnit and
historical regression workflows remain the merge gate. These repairs add no work to
immediate successful send, TryReserve, or ordinary release.

Performance acceptance compares baseline commit
`13928a35df4bff3cd99b14bd1df131aa3eb3bcdb` with the exact candidate using the same
.NET 10.0.102 SDK. Required evidence includes resolved IL, real object sizes/offsets,
Tier0/Tier1/default-tiered/FullOpts native bodies for send **and release**, and
uncongested plus saturated/cancelable allocation/throughput samples. Keep rejected
intermediate and stalled-baseline samples. The local verification report identifies
the frozen source hashes and raw results; this ADR does not replace that evidence
with an architecture-level claim of zero cost.

### Initial same-SDK measurements

The 2026-10-22 Linux x64 candidate keeps pump/session/frame sizes at 144/312/40 bytes
and uses the original task and capacity reference offsets. The intrinsic queue is 48
bytes per blocked epoch; the immutable marker is 48 bytes once at type initialization.
Measured producer allocations per actually blocked send are 560→432 bytes without a
cancelable token and 808→576 with one for a single waiter. Sixteen-waiter candidate
batches measure 387/471 bytes per send respectively. Baseline accepted 16-waiter runs
can strand and are retained as failures, not excluded to claim a throughput win.
True canceled single-waiter process allocation (including cancellation observation)
is 3816→1912 bytes; 16-waiter cancellation is 3654.5→1807 bytes per send.

The deterministic recovered-capacity schedule is 152 bytes/attempt on both baseline
and candidate, including the common thread/contended-lock harness cost; the rejected
intermediate was 184. With a canceled writer return held, both baseline and candidate
keep the capacity slot null and reach idle while the admission gate is externally
held; the rejected intermediate does not. These measurements explicitly cover the
mixed-state ordinary-release corner, not just an isolated empty session.

Resolved fast-send IL and observed non-PGO fast-entry Tier0/Tier1/FullOpts listings
match the baseline; default tiered PGO native bodies differ and are not claimed to be
byte-identical. Ordinary release retains only its existing capacity-slot null check,
now inline with the nonempty routine outlined. Native size changes in cold blocks and
allocation reductions do not establish a wall-clock throughput non-regression: local
CPU was shared, and no zero-cost or speedup claim is inferred from noisy timing.
