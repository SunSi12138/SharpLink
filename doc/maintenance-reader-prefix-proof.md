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

The entire InvocationDispatch, AdmissionDispatch, ServiceRegistration and
PreAdmissionStreams files are pinned byte-identical to dev commit
`13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`. This independently guards the
exclusion of the separate queued-registration correction and queued rejection
handoff change. It is not a claim that their unsupported-path oracle ran.
