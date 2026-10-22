# ADR 0004: Cooperative same-session implementation maintenance

- Status: Proposed, guarded first implementation
- Scope: opt-in fixed server domain, compatible implementation-only replacements, **no queued admission**
- Related: [prepared catalog transaction](0003-prepared-implementation-catalog.md)

## Decision and supported scope

**Queued-admission QoS is excluded from this first mode.** Opt-in fails if any current,
retained or physically reclaiming admission generation has waiting enabled. An empty
queue is not sufficient, and `QueueOneWayCalls = false` still allows ordinary RPC
waiting. The host must configure `MaxQueuedCalls`, `MaxQueuedBytes` and `MaxQueueDelay`
as zero, and finish any earlier waiting-capable generation cleanup, before opting in.
No queue limit or policy is silently disabled or reduced. Immediate concurrency/rate
rejection remains supported. Applications needing burst buffering, deferred limiter
admission or queued one-way calls must keep ordinary operation without this capability.
Existing normal queue support is unchanged in non-opted-in domains.

The eligibility check runs only at cold opt-in under the configuration writer gate;
all later admission configuration changes are rejected. In-flight preparation and
reentrant rollback disposal retain the existing cold writer leases. This restriction
does not replace physical ownership/fence proof. The sole reader must finish each
synchronous immediate admission/rejection prefix before reading its ingress fence.
The restriction adds no maintenance check to ordinary request admission.

A host may configure one whole server domain and a fixed roster of authenticated
client participant/runtime-incarnation identities before starting. Each participant
opts in before connecting. Every callable physical connection to this server must
belong to that roster. Pools may have multiple connections, with their existing
sizes and policies unchanged. Enrollment works during ordinary reconnect outside
an operation. Successful maintenance preserves the exact complete physical-session
cohort, transports, authentication contexts and held compatible client proxies.

The first transport mode requires actually negotiated TLS plus the existing server
authenticator establishing the roster's exact Subject. Domain and incarnation GUIDs
are correlation identities, not credentials. The host controller is local-only; no
RPC message grants authority to initiate a server catalog transaction. Existing
connection admission, authentication, authorization, frame and queue limits remain
in force. Ordinary clients cannot connect to a maintenance-configured domain.

Only implementation assemblies owning no contracts, codec registrations or binding
graph are replaceable. Contract Types, owner identities and complete dependency
closure remain exact, and service contract/protocol/codec shapes remain unchanged.
Client contracts/codecs are not replaced or reclaimed. Arbitrary immutable client
code that has not selected a session can legitimately begin a new call after resume;
this feature is not a census of all user code or all client callbacks.

This deliberately is not the broader catalog/receipt proposal: it does not provide
incompatible client upgrades, replay-intent discrimination, exact-registration
receipts, artifact attestation or atomic simultaneous availability.

## Public usage

Use `SharpLink.Server.SharpLinkServerMaintenanceExtensions.EnableCooperativeMaintenance`
on a built, not-yet-started server. Supply a nonempty domain GUID and the complete
`SharpLinkMaintenanceParticipant` roster. Keep its returned
`ISharpLinkMaintenanceController` in host code, never in an RPC service.

Use `SharpLink.Client.SharpLinkClientMaintenanceExtensions.EnableCooperativeMaintenance`
on each built, unconnected fixed-endpoint client, after registering its immutable
contract assemblies and configuring its pool. The options supply that same domain,
participant ID and a fresh runtime incarnation. Configure TLS trust and real
application authentication through the existing builders, not through maintenance.

After normal connection readiness, call `BeginMaintenance` with the exact current
`CatalogGeneration` and the complete list of locally loaded source/target assembly
pairs. Observe the returned operation with `WaitAsync`. A wait token cancels only
that observer, not the update. A returned result is successful only when its stage
is `Succeeded`; failed operations carry a failure and their precise terminal stage.

`PlanDigest` is a SHA-256 **correlation identity**, currently over the domain, server
incarnation, expected catalog generation and ordered old/new assembly MVIDs. It is
not a hash of artifact bytes, an integrity attestation, or a standalone cohort
certificate. The exact operation, validated host plan, authenticated peer identities,
physical sessions, stage order and target manifest form the actual protocol binding.

## Permanently immutable configuration

Opt-in permanently seals configuration in this first mode, not just during an update.
The following existing writer categories reject through their throwing and Try
surfaces at their actual publication locks:

- Client RegisterAssembly, ReplaceAssemblyAsync and UnregisterAssemblyAsync; shutdown
  retains its distinct internal drain path. Opt-in rejects both pending public
  unregister operations and non-Running modules with timed-out physical retirement.
- Client endpoint selection/admission/circuit breaker, retry, reconnect, request
  timeout, heartbeat, interceptors, telemetry detail, compression preferences/policy,
  flush settings and fixed/cluster pool sizing. Cluster topology itself is unsupported.
- Server ordinary registry registration/replacement/unregistration, admission program
  and call capacity, connection admission, desired-session refresh, interceptors,
  telemetry detail, compression and flush configuration.

Opt-in also rejects already-admitted cold metadata/configuration preparation and
candidate rollback disposal. These cold-only leases begin under the same registry
lock as opt-in, before callbacks, and survive physical cleanup after a timed-out
module has been removed from the registry. Cleanup failure leaves a sticky rejection
rather than a false stable-catalog certificate. Failed unpublished admission-binding
cleanup also leaves a cold sticky witness, even when no program was registered;
ordinary validation errors do not. Reentrant callbacks reject opt-in
instead of waiting while holding its locks. A failed retired-connection cleanup also
publishes the existing sticky catalog-failure bit before removing its retired owner.
Opt-in, future proof checks and the final registry-locked seal reject that failure;
no application exception or completed cleanup task is retained by this witness.

Normal Stop remains allowed and invalidates maintenance; a paused session cannot be
resurrected by Resume after shutdown. Automatic reconnect/new enrollment is allowed
outside transactions but final Ready publication is quarantined while one is active.
Session-refresh capability is not negotiated in this fixed-configuration mode.

## Ordered cold protocol

The bounded control envelope is exactly 144 bytes and uses a separately negotiated
capability. It carries stage, domain/server/participant/runtime/session/operation
incarnations, the plan correlation digest, participant session count and target
catalog generation. Reserved bytes, stage, counts and identities are strict. Frames
are unflagged and use request ID zero. The controls use the ordinary send FIFO, not
Ping/ACK priority. Their forced flush cannot overtake older normal frames.

1. Existing TLS and authentication complete, then explicit Welcome/Enroll/Enrolled
   binds each physical session before Ready. A new connection cannot publish Ready
   through a maintenance writer. Whole-domain capture rejects incomplete or retiring
   owned sessions and missing roster participants.
2. Freeze each participant's complete local pool under its existing publication gate.
   Existing Ready selection/reservation double checks reject further selection.
   Join reservation -> pending/auxiliary ownership and the final compression ACK.
   Cancelled pending identities continue to forbid late Request publication even if
   their immutable codec eventually returns after resume.
3. Send an ingress fence on each ordinary FIFO. The server reader records the fence
   synchronously and moves only the protocol phase to MaintenancePaused. It never
   waits for a drain. Lifecycle Ready remains available to already-admitted ordinary
   invocation/stream progress, while subsequent Request frames are rejected.
4. Join existing global/per-session request permits, pending admission transfers,
   every admission generation and reclaim/disposal owner, stream/dispatcher/module
   owners and deferred connection cleanup. Once every call map is empty, join the
   existing deadline-scan owner and exceptional deferred cancellation-callback owner,
   then capture intrinsic backpressured sends. Admission reclaim stays retained until
   physical disposal completes. A throwing disposal is not a successful drain.
5. Send the reverse ordinary FIFO fence. Client readers consume all preceding
   responses/data, join their existing auxiliary/stream/send owners, and send a final
   ordinary-queue acknowledgement. Only this complete proof can materialize and
   irreversibly retire the old server implementation transaction. After all candidate
   callbacks, the typed proof revalidates operation, cohort and current authentication
   immediately before the no-callback registry-locked seal. This final observation is
   the retirement authority linearization point; later authority loss fails closed,
   not backwards into rollback. Application-defined clock sampling precedes the
   cohort check and runs outside registry locks.
6. Publish the prepared target while paused. Every client verifies that its applied
   manifest has the exact target generation and the unchanged complete contract/hash
   map, then acknowledges Commit. **All participants commit before any Resume.**
7. Restore the server protocol phase and authorize each complete client pool to
   resume. Wait for every Resume acknowledgement, revalidate the cohort, and release
   the cold writer. Availability is restored in order, not atomically everywhere.

Authentication Subject/expiry is revalidated across the potentially unbounded drain,
retirement, commit and resume boundaries using the server runtime TimeProvider.
Stale, replayed, mismatched or out-of-order controls close the affected operation.

## Physical ownership, not zero-count folklore

A pump snapshot joins only intrinsic pending enqueue nodes already published. It is
not itself a producer seal or universal session-quiescence certificate. In this
restricted mode, immutable admission programs cannot suspend for queue admission.
The sole FIFO reader therefore finishes each immediate acquisition/rejection prefix,
including synchronous rejection-send publication into intrinsic pump ownership,
before reading the ingress fence. No separate queued-generation dispatch or parent
handoff correction is included in this mode. The cold eligibility and writer seals
are essential to that argument; an empty queue alone would not establish it.

Exceptional framework-owned cancellation notification publishes its physical owner
before offload, retaining it through synchronous registered callbacks and physical
lease cleanup. After ingress and ordinary request/map closure, the coordinator joins
the existing deadline-scan owner before capturing deferred callback ownership, then
captures pump ownership. This order also observes cleanup failure published by a
finishing scan. No new owner is acquired on successful ordinary calls. Completed
callback tasks and application exceptions are not retained by the cold owner.
Async-void callbacks or work deliberately detached by application code remain outside
this physical-completion guarantee.

This cut concerns old server invocation and owned disposal. Detached immutable client
consumer callbacks may still finish or emit a harmless late Cancel. They cannot mint
a new Request from a cancelled pending identity. Server retired connection scopes
remain in SnapshotOwned until their physical cleanup completes and therefore cannot
silently escape the whole-domain certificate.

No update-specific branch, counter, epoch or wrapper is inserted into ordinary RPC
selection, reservation, pending publication or generated invocation. Enrollment data
lives in opt-in-only weak cold owners rather than changing ordinary server, client,
session or connection instance layout. A below-maximum pool does not perform a
maintenance lookup in its ordinary expansion-selection helper; only an actual cold
worker and final topology publication check the freeze. Existing Ready/lifecycle comparisons supply the pause barrier. Parser and
reader switch changes still require same-mode native/performance verification.

## Failure and acceptance evidence

Preflight rejection before Freeze releases its prepared state and leaves availability
unchanged. Once Freeze starts, any failure conservatively fails the entire domain
closed, even before retirement. Failure after old retirement never rolls back or
reopens. Stop is always available for terminal cleanup. Uncooperative physical work
can keep an operation incomplete indefinitely; observer cancellation is not evidence
that it drained.

Targeted tests exercise real TCP/TLS with two runtime participants and two physical
connections each, all invocation shapes and held proxies, cancelled observers and
cancelled non-cooperative old invocations, physical asynchronous stream disposal,
independent-domain availability, stale plans, replay/incarnation/digest errors,
partial-commit disconnect, authentication expiry, reconnect/new-peer rejection,
Begin-versus-Stop, reentrant metadata authority, and immutable writer rejection.
Additional ownership tests hold an immutable cancelled client serializer through
successful resume, retain completed public receipts while collecting the old
implementation ALC, join physically detached admission generations, and reject
opt-in during post-removal catalog cleanup, including cleanup failure.
Protocol unit tests cover fixed bounds, reserved bytes, request IDs/flags and paused
progress frames. These tests do not replace the separate performance/native audit or
claim that an unavailable prerequisite-specific regression oracle has run.
