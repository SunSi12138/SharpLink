# Protocol3 S1 physical-owner collision — source gate, not validation

This note records an **unimplemented ownership boundary** in the unbuilt S1 source
preparation based on composite `ebc8f7ac`. It is not an acceptance report. Held
changes 931/933/964 have **not** been imported or proven by these edits.

## Necessary fences now represented in source

- One `ServerBindingAdmissionGroup` closes admission for all members at one cut.
  Scalar controls detach payloads after that cut. Its drain task cannot complete
  before both all counted leases are released and all member payloads are detached.
- Unregister publishes service removal and a checked positive generation at the
  cut, while detached services/codecs remain owned by cleanup. Replacement keeps
  its separate already-selected old group. Cleanup waits module and binding-group
  drains; stop also closes static groups before service/provider disposal.
- Retired slot scans carry only full scalar tokens outside their table gate. One
  coalesced server worker sends bounded best-effort invalidations outside the
  registry gate. Dropping a notification never opens the server retirement gate.
- A completed Bind's later Cancel is a no-op. A nonterminal Cancel can only target
  its exact creator ID. The current receive loop awaits Bind result publication,
  so it does **not** demonstrate receiving cancellation concurrently with a pending
  Bind commit. No speculative parallel receive architecture was added.

These fences protect **the owners actually counted**. They do not prove that every
physical worker is currently represented by a binding lease.

## Blocking collision: logical dispatch completion is not physical completion

`SharpLinkServer.RequestLoop.DispatchRequestAsync` acquires the exact binding and
method facts, but releases its `ServerBindingLease` in its own `finally` after the
returned dispatch `ValueTask`. Deferred admission and
`SharpLinkServer.PersistentDecodeDispatch` can transfer work to
`ServerDecodeExecutor`; logical cancellation/reconciliation may finish before the
physical worker or later invocation/stream cleanup. Simply awaiting group drain
does not repair an owner that was released at the wrong boundary.

Before full S1 lifecycle acceptance, a separately reviewed integration must:

1. Move (not copy) the exact binding lease from request admission into the unique
   physical call owner, such as `ServerRequestPermit`/its owning call state. That
   owner retains the selected `ServiceRegistration`, frozen `RpcMethodFacts`,
   original Session/token, and module identity. No later registry lookup is allowed.
2. Keep ownership through queued admission, retained input/prefix ownership,
   persistent decode execution, reconcile handoff, generated invocation, stream
   tails, user cleanup, and real provider completion. Cancellation and task state
   are not certificates that borrowed bytes or provider work are quiescent.
3. Give every transfer/rejection/failure path one exactly-once terminal release.
   A readonly lease struct copied into two continuations is not a transfer protocol.
   If separate logical and physical owners are required, their counted uses and
   final join must be explicit; group drain may only observe the final physical cut.
4. Reconcile the held 931/933/964 source collisions explicitly. Do not infer their
   physical-reader, asynchronous-provider, or cleanup guarantees from a scalar
   table test or from the fact that a Task completed.

## Required real-component evidence, still absent

Pause the actual persistent decode provider after input ownership starts; cancel,
replace/unregister, and stop the Session while it remains paused. Prove old exact
registration/facts/input/module remain owned and no service/codec/provider cleanup
runs. Release the provider, then independently pause invocation and cleanup tails;
prove the same fences hold until each physical owner really terminates. Include
repeated cancellation, slot reuse, terminal Session, and no double release.

Separately exercise real normal-queue barriers: Invalidate-before-BindResult,
invalidated early success before enqueue confirmation, dropped invalidation with
newer-incarnation result, queue saturation, and timer construction/arming failure.
Current added table/group/cleanup test **source** covers only those concrete
components; it is neither an executed result nor a substitute for these tests.
