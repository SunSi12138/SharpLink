# No-queued-admission reader-prefix ownership argument

This argument applies only to the explicitly enrolled cooperative-maintenance
capability. It is not a proof for an arbitrary server, queued admission, arbitrary
detached application work, or all of issue #775.

## Capability boundary

Opt-in rejects every current or still-retained admission generation that can
wait, including programs that reject OneWay calls but queue calls with replies.
The configuration writer seal also excludes a preparation already in progress;
later admission writes cannot publish a waiting program. Failed unpublished
cleanup is sticky and prevents certification. The limit is a capability check,
not a snapshot of an empty queue, and is not implemented by silently disabling
configured queues.

## Ownership before the next FIFO frame

1. With the immutable queue bound equal to zero, the non-async admission
   controller returns an already-completed `ValueTask` on every normal branch.
   Partition callbacks and any exceptions execute synchronously. There is no
   suspended queued continuation in this capability.
2. The sole request reader calls `DispatchRequestAsync` inline. Its synchronous
   prefix either reaches ordinary dispatch or starts rejection publication
   before the reader can parse the next frame.
3. Successful dispatch acquires the existing request permit before asynchronous
   service acquisition or persistent decoding. Existing owners retain the
   corresponding asynchronous work through its resource cleanup.
4. Permitless rejection invokes the error sender synchronously. Its packet
   construction and session enqueue prefix reach the non-async send pump before
   returning an incomplete `ValueTask`.
5. A backpressured enqueue publishes its intrinsic pending node before that
   incomplete return. A synchronous closed/rejected enqueue returns its packet
   resources before returning. Thus the later FIFO fence observes either
   completed rejection publication or an existing physical send owner.

The argument covers calls with replies, OneWay calls, immediate admission and
capacity rejection, malformed/decode failures, and synchronous policy failures.
An asynchronous queued-admission continuation does not have this proof and is
outside this first capability. No claim is made that its separate handoff
regression was executed or repaired here.

## Separate physical tails

FIFO parsing alone does not certify completion. The coordinator must also join
existing invocation, stream, producer, pending-send, and service cleanup owners.
Framework-offloaded cancellation callbacks and scheduler callbacks require their
separate physical cancellation owner and tests. A callback that does not return
may prevent completion; a timeout is not permission to reclaim its resources.

## Client outgoing producer closure

Before its ingress fence, each frozen client waits its existing selection reservations
and pending/auxiliary ownership. Nonproducers release selection ownership after pending
registration and before serialization. Outgoing client-stream, one-way-stream and duplex
groups instead retain that same reservation through the whole writer task, including
sequential enumerator disposal. There is one reservation per RPC, never per parameter.
Generated multi-parameter writers use Task.WhenAll, which waits every sibling even when
one faults. Startup failure releases locally; a returned producer task owns the release
before its supervisor registration. Early response/cancellation cannot steal it.

The removed internal registration notification had one production implementation and
one action: release this reservation. No replacement hook or successful-call counter is
introduced. The six registration call sites, their exception exits and group cardinality
have explicit unit coverage, including an unrelated held reservation and refresh cuts.
The real four-TLS-session held-disposal control prevents old implementation retirement
until producer release, then demonstrates old ALC collection with live sessions/proxies.

This does not join arbitrary immutable client unary serializers or interceptor callbacks
that no longer own pending identity. Their cancellation identity still rejects late
Request publication. The reverse fence joins receive/auxiliary/send ownership; outgoing
producer closure was established before the ingress fence, not by a supervisor snapshot.

## Change detection, not a formal proof

`python3 eng/test-maintenance-prefix-proof.py` verifies twenty-nine reviewed source
regions, including the actual reader, immediate admission/dispatch, persistent
decode, sender prefix, intrinsic pump enqueue, and the client reservation/producer
handoffs, generated Task.WhenAll group, and both cold fence predicates. It also checks the reader's
inline Request branch. These conservative pins deliberately require review even
for harmless source edits. They do not prove the argument automatically.

When a pin fails, inspect every newly introduced await, offload, callback, and
resource transfer in that region and rerun the relevant semantic tests. Do not
update a digest merely to silence CI. In particular, a new await before an
existing owner is acquired invalidates this capability's admission proof.

## Current-dev integration of queued identity and intrinsic sends

The integrated tree includes #930. The no-waiting argument is about reachable
branches, not exclusion of that correction from the repository:

- `DispatchRpcAsync` changes only the incomplete-admission branch to call
  `TryCopySelectedAdmissionPayload`. `DispatchOneWayRpc` makes the same change;
  its queued continuation methods move to `QueuedAdmission`. Neither immediate
  prefix adds an await, offload, weak-identity lookup, or maintenance lookup.
- With all current, retained and physically reclaiming waiting-capable programs
  excluded at opt-in, `AcquireAsync` returns a completed successful ValueTask for
  immediate acquisition and rejection; no asynchronous cancellation wait is entered.
  A partition-selector throw
  is caught synchronously; reply rejection enters the existing physical sender
  before the reader can consume another frame. The queued-only payload copy and
  admitted dispatch continuations are therefore unreachable for enrolled calls.
- `ServiceRegistration` allocates a weak identity when a dynamic registration is
  constructed. It adds no call lease and does not root its target. The changed
  disposed-registration error classifier executes synchronously; it does not
  postpone rejection or acquire a different registration. Existing dynamic
  acquire/release and request-permit ownership remain in place.
- `PreAdmissionStreams` adds selected-service capture to the queued-only copy.
  Immediate rejection still uses the original bounded discard/termination paths;
  it does not re-enter queued dispatch or borrow a replacement registration.
- Intrinsic send ownership now preserves synchronous telemetry reentrancy.
  The final reserve recheck reloads a queue published by its callback before
  updating the wake slot. Resume claims a node before reservation telemetry,
  restores waiting only on a failed reserve, and follows its live successor
  after callbacks. The claimed node remains physically owned through return and
  cancellation deregistration. The whole backpressure-owner file is pinned,
  including resume/retirement, rather than only the enqueue prefix.

`AdmissionMaintenancePrefixTests` exercises inline grant and rejection for all
non-waiting policy forms. The retained-generation opt-in tests cover the seal;
`BackpressuredSendOwnershipTests` covers cancellation, Stop, sibling mutation and
new-waiter publication from reservation callbacks. Queued replacement and
retirement tests continue to validate #930 outside maintenance. The existing
admitted-dispatch parity guard is retained; passing it does not certify queued
admission as eligible for this maintenance capability.
