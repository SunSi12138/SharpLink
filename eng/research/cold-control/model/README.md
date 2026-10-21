# Cold-control ordering model and independent API review

## Verdict

A coordinated, cooperative, stop-the-world update of a fixed session cohort is a
coherent possible architecture. The bounded models below support its **ordering**,
not its present implementation in SharpLink, its liveness, or zero hot-path cost.
The concrete missing prerequisite is a trustworthy **physical ownership fence**.
Logical call completion, pending-table emptiness, or dynamic-module lease count
zero must not be substituted for that fence without a complete ownership proof.

An implementation-only, same-wire-hash server replacement is a useful but different
feature. It must not be described as solving #775's general exact-registration,
retired-binding, wire-changing, or coordinated-update requirements. A local
quiescence handle is likewise only a local building block unless the framework
also enforces the full session-cohort protocol.

Baseline reviewed: `13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`.
Models run using Python 3.12.14, standard library only. No production source was
modified to produce these models.

## Reproduce

```sh
python3 model.py
python3 fence_model.py
```

Each checker exhausts its finite reachable state graph without a time/state cap.
Its breadth-first traversal records a shortest counterexample for each weakened
variant. Each checker writes full traces and counts to `results.json` and `fence-results.json` in its working directory. These outputs are generated afresh in the public research CI; local archived outputs are not included.

### Coordinator results

| Correct bounded scenario | Reachable states | Successful terminal states |
|---|---:|---:|
| Two fixed participants, one update, new pooled connection, caller cancellation, disconnect | 7,725 | 72 |
| Two sequential updates with delayed/replayed old messages | 425 | 2 |
| Disconnect/reconnect with stale-incarnation message replay | 3,033 | 2 |
| Local candidate with mismatched digest is rejected | 463 | 2 |

All four preserve the checked safety invariants. The success states prevent the
claim from being vacuously based on refusing every update. The following eight
mutants each produce a concrete counterexample:

1. Resume after only one participant's commit acknowledgement.
2. Publish a new pooled connection while the participant cohort is frozen.
3. Reopen a permanently retired old gate when a caller cancels its wait.
4. Issue a fence certificate on logical rather than physical completion.
5. Accept a stale acknowledgement without binding operation and digest.
6. Accept a message for an invalidated connection incarnation after reconnect.
7. Prepare/commit differing candidate digests under one operation.
8. Permit two update writers to own the same registry/cohort.

The stale-ack mutant reaches generation 2 with participant 0 resumed while
participant 1 has not committed generation 2. It does not require fabricated
messages: old genuine Prepared/Fenced/Committed messages are sufficient when
identity checks are removed.

### Bidirectional physical-fence results

The correct two-producer, one-session FIFO model exhausts **1,350 states** and
reaches four successful terminal states. Six weakened variants fail:

1. A logically abandoned client producer is allowed to serialize/enqueue after a
   prematurely issued fence.
2. Server acknowledgement on parsing the fence races earlier asynchronous RPCs.
3. Logical server completion hides generated stream/send/dispose work.
4. Non-FIFO delivery allows a fence to overtake an earlier request.
5. Parsing FenceAck is mistaken for completion of asynchronous response decoding.
6. Logical client response-consumer abandonment hides retained codec/buffer work.

The required sequence is therefore not merely "send Ping and await Pong":

1. Close producer admission and physically drain all preceding producers.
2. Enqueue a fence in the same FIFO domain as their request/stream frames.
3. After parsing it, physically drain preceding server dispatch, queues, generated
   producers, response sends, and disposal; enqueue FenceAck after their output.
4. After receiving FenceAck, physically drain preceding client response/stream
   decoding and consumer ownership before issuing the local certificate.

OneWay is included by the producer/remote-work abstraction: local enqueue success
is not remote business completion. Duplex or noncooperative streams may prevent
the fence from completing indefinitely; timeout must not fabricate a certificate.

## What is and is not modeled

The coordinator has two fixed participants and at most two sequential operations.
Each operation has a unique identifier, one common expected digest, and a fixed
participant-incarnation tuple. Prepare, Fence, Commit, Resume and their replies are
independently delayed messages. Old genuine messages may be replayed. The model
includes caller-wait cancellation, partial commits, disconnect/reconnect, exclusive
writer ownership, permanent old-generation retirement, and a quarantined new
connection. New-connection admission is tested in the one-operation scenario; a
subsequent operation with that connection as a third cohort member is not modeled.

The physical-fence model has two bounded producers and two asynchronous remote
jobs/response consumers. Their states are *observational model state*, not a
proposal to add per-RPC counters or fields. The coordinator's `work` state is an
abstract complete physical-ownership oracle, refined in the separate FIFO model.

Assumptions and limits:

- Participants and coordinator are cooperative and authenticated/authorized. The
  model does not establish cryptographic receipt authenticity, hash collision
  resistance, or authorization of remote management commands.
- The correct implementation must have atomic cold writer/cohort operations and
  sound existing admission gates. The model is not a .NET memory-model proof.
- No crash-recovery durability, distributed consensus, fairness, eventual
  delivery, unbounded topology, performance, or resource-exhaustion proof is made.
- A rejected preparation can leave the abstract operation waiting; safe recovery
  policy is a separate API obligation. No automatic rollback is assumed.
- Disconnect aborts the coordinator and invalidates that connection incarnation.
  It cannot retract an already transmitted Resume from another participant.
  Consequently the safety contract is generation coherence, **not simultaneous
  global availability**. All committed participants may resume at different times.
- Unchanged source shape is not proof of unchanged JIT code or contention.
- A stale managed reader that still roots a Stub/codec is not native use-after-free.
  It can delay collectible ALC collection even after durable registry owners drop.

## Minimum useful local public API

Names below are conceptual, not a proposed source-compatible API commitment.

1. **PreparedLocalUpdate**. Validate/create the complete candidate service,
   contract, codec, and dependency closure while the old generation is usable.
   Bind it to exact expected old registration objects and a registry revision.
   Preparation failures have no publication side effects. Before irreversible
   closure, reacquire exclusive cold writer ownership and reject stale preparation.
2. **LocalQuiescence operation**. Close the exact old module's existing admission
   gate permanently, then expose completion only after proven physical ownership
   drains. The runtime owns the operation independently of the caller's waiter.
   A timeout or cancellation never reopens the retired module.
3. **Commit(prepared, quiescence)**. Accept only matching, framework-issued opaque
   handles for the same operation, registry revision, registrations and digest.
   Commit is one-shot and accepts its *owned* already-retired old module. Current
   Replace rejects non-Running old modules, so composing Unregister then Replace
   does not implement this transaction. No fallible candidate setup may remain
   between permanent closure and the intended atomic publication boundary.
4. **Published receipt and independent reclamation observation**. Publication,
   admitted physical-work drain, removal of durable registry/cache owners, and
   eventual ALC collection are distinct events. The receipt must not turn a
   bounded timeout into "all references released" or promise collection while
   application or temporary reader roots still exist.

This API can honestly promise local ownership and publication. It cannot promise
remote receipt, complete pooled topology coverage, exact remote registration,
global quiescence, or simultaneous resume. Existing replacement already separates
successful publication from `ReferencesReleased`; merely wrapping that result is
not a new architecture.

## Minimum useful end-to-end coordinated API

An application can supply artifacts and select the update time without SharpLink
inventing an artifact distributor. But the following runtime protocol evidence
cannot be replaced by application-provided booleans such as `allDrained = true`:

1. An **authorized maintenance operation** establishes a unique operation ID and
   common prepared-plan digest. Per-participant receipts additionally bind exact
   local registration identities. RpcAssemblyHash alone is wire shape, not exact
   registration identity. Preparation includes the entire codec dependency closure.
2. An **exclusive cohort lease** freezes all affected connection/pool publication,
   admission generations, and conflicting registry mutations. The cohort includes
   *every session able to admit the affected server registration*, not just one
   initiating client's pool. Newly connecting sessions stay quarantined until
   they bind the committed generation, or are explicitly rejected. Pooling and
   topology cannot silently be reduced to one connection.
3. **Framework-issued quiescence certificates** identify operation, digest,
   participant, and connection incarnation and attest the bidirectional physical
   fence above. Old local proxies retain permanently retired module ownership;
   retries and stale snapshots cannot silently forward old work into the new epoch.
4. **Commit receipts** are accepted only for that fixed cohort and identical plan.
   Resume is authorized only after all fixed participants actually committed.
   Receiver-side identity validation remains necessary even if coordinator ACK
   validation is correct.
5. **Resume new ownership**, never old ownership. Existing monotonic
   Ready/Draining/Closed state must not simply be reset to Ready. A new immutable
   session/domain or a separately specified cold lifecycle transition is needed.
6. **Owned operation status/completion** remains observable if the initiating
   waiter cancels. Pre-cut preparation failure may safely abort. After permanent
   retirement or partial commit, no automatic rollback/reopening is promised;
   retain the closed/quarantined state and reconcile or establish fresh sessions.
7. **Security and scope** remain explicit. Existing authentication is preserved;
   new control frames do not grant an ordinary RPC client server-wide management
   authority. Same-session reuse assumes cooperative framework producers. With
   identical wire method IDs and no per-request generation, a deliberately replayed
   payload from an arbitrary peer cannot be classified as an old registration by
   its bytes alone. A permanently retired immutable session/domain gives a
   stronger boundary than cooperative same-session maintenance.

A trusted application coordinator may drive these stages, but a library exposing
only local handles must say that the application owns the distributed safety
contract. A true framework end-to-end API must itself validate the receipts,
cohort, topology freeze, identity, and resume conditions. This distinction avoids
claiming an end-to-end result from safe but insufficient local primitives.

For a **dynamic-only** route, list the limitation in the API and documentation:
all affected callers need revocable, exact local dynamic-module ownership. Static
or external clients that cannot participate must stay on an immutable old domain
or be excluded, not silently treated as upgraded. Unrelated static traffic may
also pause during session-wide maintenance; this cost is explicit.

## Source evidence and concrete blockers

At the baseline above:

- `doc/public-rpc-semantics.md:125-134` promises current server replacement
  semantics and distinguishes collectible client contract replacement.
- `src/SharpLink.Client/SharpLinkClient.ContractBinding.cs:42-45` intentionally
  does not rebind/invalidate held proxies on a later manifest refresh.
- `doc/rpc-wire-identity.md:34-44` validates wire hashes at Get/bind and describes
  the held-proxy boundary, not per-request exact registration validation.
- `src/SharpLink.Server/SharpLinkServer.AssemblyRegistration.cs:258-298` performs
  codec/map publication and route snapshot publication before beginning old-module
  drain. Reordering the last two operations alone does not make all preceding
  fallible setup an atomic prepared transaction.
- `src/SharpLink.Runtime/SharpLinkDynamicModule.cs:257-287` supplies an existing
  double-checked admission lease and permanent Running-to-Draining gate. Reusing
  this ownership is promising, but its lease lifetime must cover real physical
  work, not only logical completion.
- `src/SharpLink.Client/ClientConnection.cs:202-246,456-458` transfers selection
  reservations into pending-call ownership. Thus reservation zero plus pending
  zero is not an independent serializer/producer completion proof.
- `src/SharpLink.Client/SharpLinkClient.Invokers.cs:432-456` discards the unary send
  ValueTask and returns the pending operation. `ObserveAbandonedEmission` at
  lines 501 onward observes errors, not a globally awaitable producer fence.
- `src/SharpLink.Server/SharpLinkServer.RequestLoop.cs:274,381-430` dispatches
  requests asynchronously. FIFO parser progress alone is not dispatch completion.
- `src/SharpLink.Runtime/SharpLinkGenerationRetirement.cs` already establishes
  runtime-owned cleanup with caller-only wait cancellation.

The independent baseline queue-cancellation reproduction is sealed separately at
commit `2fbf04bc8c89c5ffa86a7d07544cb318d2b0fadd` (test-only). It uses identical
service wire shape and the same contract Assembly object in a new service ALC.
A queued call captures R1 cancellation ownership, then after replacement invokes
R2 under an R2 module lease. Forced R2 drain does not cancel the handler's R1 token.
The no-replacement control passes. This is a concrete ownership mismatch, not a
timeout or fixture failure, and must be reconciled before treating queued-call
drain as an exact-generation fence.

Independent producer/stream abandonment reproductions from other reviewers are
not duplicated here. Their exact artifacts/results must be attached separately
before they are asserted as tested production facts.

## Acceptance gates before a production claim

1. Physical ownership audit covers unary, OneWay, client/server/duplex streaming,
   queued admission, retries, cancellation/deadline races, deferred serialization,
   response dispatch, stream MoveNext/Send/Dispose, and ALC/cache owners.
2. Deterministic tests cover every model counterexample and actual runtime race,
   including multiple clients and pooled connections, mid-update connection
   creation, stale operation replay, disconnect after partial commit, and waiter
   cancellation. No elapsed-time sleep is the correctness oracle.
3. The complete supported CI matrix and existing semantic/performance budgets
   pass. Any sandbox limitation is separately recorded, not counted as a pass.
4. Ordinary non-dynamic APIs and representative JIT/native hot paths are compared
   under normal production settings. No new version checks, wrappers, counters,
   frame fields, or participation branches are hidden behind a cold API name.
5. If existing leases are retained longer to cover physical completion, quantify
   changed contention and drain behavior. Equal source operations do not prove
   equal runtime cost, and no benchmark can repair incorrect lifetime ownership.
6. The final public issue/PR explicitly distinguishes proven scope, unimplemented
   obligations, and rejected shortcuts. A narrow same-wire fix is not closure of
   the whole exact-registration architecture question.
