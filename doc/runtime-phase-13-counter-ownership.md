# Runtime Architecture Phase 13: counter and lease ownership

This document is the executable ownership reference for issue #81. It records
what each count means, who is allowed to acquire it, and the one terminal path
that releases it. Similar values are deliberately not automatically merged:
each retained count has a distinct reader or terminal guarantee.

## Ownership matrix

| Name | Scope / owner object | What exactly is counted | Increment / acquire function | Unique terminal release function | Can acquire fail after partial acquire? | State transition that blocks new acquire | Drain, selection, or metric reader | Underflow behavior | Hot-path frequency |
|---|---|---|---|---|---|---|---|---|---|
| Logical invocation count | one `SharpLinkClient` | one user-visible call from public `Invoke*` entry until the returned `ValueTask` completes or the returned async stream completes/disposes; it includes wait-for-ready and retry backoff | public `InvokeUnaryAsync`, `InvokeOneWayAsync`, `InvokeClientStreamingAsync`, `InvokeServerStreamingAsync`, and `InvokeDuplexStreamingAsync` | `CompleteLogicalInvocation` wrapper / `LogicalInvocationAsyncEnumerable.Complete`; synchronous construction failures release in the same public entry | Yes: wrapper construction can throw after increment; the entry catch releases once | client stop rejects future entry; an already-counted invocation remains visible until its own terminal completion | `ISharpLinkClientDrainInspector` in multi-cluster retired-client drain | wrapper/stream enumerator has a one-time terminal guard; focused tests assert exact zero after terminal paths | once per logical user call, never per frame/item |
| Physical connection call count | one `ClientConnection` | one published `PendingRequestTable` slot, or one explicitly paired one-way call without a pending response slot | `IPendingCallOwner.OnPendingCallRegistered`; `TryBeginUntrackedCall` for no-response one-way calls | `IPendingCallOwner.OnPendingCallCompleted` after its winning pending completion; `EndUntrackedCall`; failed `TryBeginUntrackedCall` rolls back itself | Yes: untracked admission double-check rolls back after increment; a pending registration can immediately terminal-complete after publication | `ClientConnection.MarkDraining` plus `RpcSession.MarkDraining`; `TryBeginUntrackedCall` rechecks after increment | P2C and least-loaded connection selection, retiring-connection cleanup, endpoint candidate load | `ReleaseActiveCall` throws if negative | once per physical pending attempt / one-way send, never per frame/item |
| Session active request count | formerly `RpcSession._activeRequests` | none in production | none | none | not applicable | not applicable | none | removed in this phase | removed: it duplicated neither drain nor protocol state |
| Pending server call admission | one `SharpLinkServer` | a transient admission that passed the initial `Running` check; it is held before any connection slot and ends only after the admission has published a global slot or rolled every provisional slot back | `SharpLinkServer.TryAcquireCall` increments before its second `Running` check | its `finally` calls `EndPendingCallAdmission` exactly once | Yes: the second state check, connection capacity failure, global capacity failure, and post-global drain check all exit through that `finally` | `SharpLinkServer` leaves `Running`; a post-stop entrant can increment only before its second check and therefore acquires no slot | only `_callsDrained` gating; it is deliberately not a business-call metric or capacity/telemetry value | `EndPendingCallAdmission` throws if negative | once per server admission, never per frame/item |
| Server connection active calls | one `ServerConnectionState` | accepted server invocation currently consuming the per-connection capacity slot | `ServerConnectionState.TryAcquireCall` after `Ready` is observed | `SharpLinkServer.ReleaseCall`, reached through `ReleaseDispatchResources` / admission-dispatch terminal cleanup; it releases this local slot before the paired global slot can publish server drain | Yes: capacity overflow or a `Ready -> Draining` race rolls back the provisional connection count through the common paired release | `ServerConnectionState.MarkDraining` changes lifecycle from `Ready` | per-connection capacity, connection service cleanup drain, stop diagnostics | `ServerConnectionState.ReleaseCall` throws if negative | once per accepted server invocation |
| Server global active calls | one `SharpLinkServer` | accepted server invocation currently consuming the server-wide capacity slot | `SharpLinkServer.TryAcquireGlobalCall`, only after the pending-admission owner and connection slot succeeded | `SharpLinkServer.ReleaseCall` owns every paired global decrement: it releases the connection slot first, calls `ReleaseGlobalCall`, then asks the combined drain predicate to signal | Yes: global capacity overflow leaves both provisional slots with the caller until the same `ReleaseCall` releases local then global; the pending admission remains held until that rollback completes | `SharpLinkServer` leaves `Running` | server-wide capacity, graceful stop, forced-stop diagnostics and the combined `_callsDrained` predicate | `ReleaseGlobalCall` throws if negative | once per accepted server invocation |
| Dynamic module call leases | one `SharpLinkDynamicModule`, striped by processor | one in-flight dynamic assembly invocation | `SharpLinkDynamicModule.TryAcquire` | the single lexical/dedicated owner of `SharpLinkDynamicModuleLease.Dispose` (`ServiceLease`, dynamic singleton wrapper, or client dynamic-channel wrapper) | Yes: a `Running -> Draining` race immediately releases the stripe it incremented | `Running -> Draining`; unregister/replacement waits for drain | unregister/replace result, forced cancellation, collectible ALC lifetime | `Release` throws on call underflow | once per dynamic route lease; striped to avoid global contention |
| Dynamic module stream leases | the matching `SharpLinkDynamicModule` stripe | the streaming-route subset of in-flight dynamic assembly invocations | `SharpLinkDynamicModule.TryAcquire(stream: true)` after its call lease increment | the same single `SharpLinkDynamicModuleLease.Dispose` terminal owner | Yes: the same post-increment state recheck rolls back both the stream and call increments | `Running -> Draining`; new streaming route lease is rejected | unregister/replace drain and collectible ALC lifetime; `RemainingStreams` diagnostics | `Release` throws on stream underflow | once per streaming dynamic route, striped with its call lease |
| Business stream count | one `StreamManager` | registered receive-stream dispatcher, not an executing dispatch operation | `StreamManager.Register` after registry acceptance | exactly one of `Unregister`, `CompleteStream`, `CompleteStreamAfterDispatchesAsync`, `CompleteRequestStreams`, `CompleteAll`, or terminated-registration cleanup removes the entry | Yes: duplicate registration and termination-after-register undo the increment | manager termination publication prevents registration; request/stream remove closes the entry | client graceful drain, server stop diagnostics, telemetry | test-boundary invariant rejects a negative count | once per register/remove lifecycle, not per chunk |
| Dispatcher dispatch lease | one `StreamManager.DispatcherEntry` | a lookup that acquired an entry and may still decode or invoke a dispatcher | `DispatcherEntry.TryAcquire`, called only after request/stream lookup | `CompleteDispatch` / `AwaitDispatchAsync` or the corresponding pre-admission `finally` calls `DispatcherEntry.Release` | Yes: `Close` can win after lookup but before dispatch completion; the acquired lease remains valid until release | `DispatcherEntry.Close` atomically blocks further acquire; `Detach` waits for prior leases | `WaitForDispatchesAsync`, detach/reuse barrier, consumer-abandon completion order | encoded state detects underflow and throws | once per inbound stream dispatch, no cross-stripe/global lock |

## One call across the ownership domains

For a normal retried unary client invocation, the timeline is:

1. `SharpLinkClient` increments the **logical** count before wait-for-ready or
   endpoint selection.
2. The chosen `ClientConnection` publishes a `PendingRequestTable` slot; its
   owner increments the **physical** connection count exactly once.
3. Response, cancellation, deadline, disconnect, or GoAway removes that slot
   with the table's single compare/exchange winner. The winner invokes the
   connection owner once, releasing the **physical** count.
4. A retry may repeat steps 2–3 on one or more physical connections while the
   logical count stays at one.
5. The outer `ValueTask` completes and releases the **logical** count once.

The server counterpart is intentionally different. A transient pending-admission
owner closes the local-to-global handoff: it begins before any connection slot,
and ends only after a global slot is published or every provisional slot is
released. The per-connection and global capacity slots then stay held until the
response send or terminal dispatch cleanup completes. They are not Session
protocol counts and are not interchangeable with client counts.

## Terminal winner tables

### Client pending call

| Terminal cause | Winner | Loser behavior | Physical release | Logical release |
|---|---|---|---|---|
| Response success / remote error | `PendingRequestTable.TryTakeMatchingCall` | late response is bounded/logged; late terminal requests are no-op | owner completion callback | outer `ValueTask`/stream wrapper |
| User cancellation | cancellation registration removes the same slot | response/deadline/disconnect see no slot | owner completion callback; streaming cleanup may first await dispatch drain | outer wrapper |
| Deadline | deadline scan removes the same slot | all other terminal attempts see no slot | owner completion callback | outer wrapper |
| Connection failure | `FailAllPendingRequests` takes each published slot | late frames are bounded/no-op | owner completion callback | outer wrapper |
| GoAway / draining rejection | request is not published or the pending slot completes with GoAway | no second owner callback | callback if published | outer wrapper |
| Consumer abandonment | `TryComplete` wins, or joins an already-winning completion before sending late Cancel | losing path only unregisters/sends bounded late Cancel | owner callback, after required dispatch cleanup | async stream wrapper on disposal/completion |
| Send failure | send path terminal-completes the already published slot | any later terminal path sees no slot | owner callback | outer wrapper |

### Server invocation

| Terminal cause | Winner | Counter action |
|---|---|---|
| Normal return / handler throw | dispatch response completion or `finally` | `ReleaseDispatchResources` releases both capacity slots once |
| Cooperative cancellation / deadline / disconnect / forced stop | `ServerCallCancellationState` terminal claim controls response behavior | dispatch terminal cleanup releases both capacity slots once |
| Admission reject before capacity acquire | rejection path | neither capacity counter was acquired |
| Stop after the first `Running` check but before pending admission publication | the immediate second `Running` check | no slot is acquired; this transient can never become a call after drain has published |
| Per-connection capacity reject | `TryAcquireCall` | its local provisional increment is rolled back before return |
| Global capacity reject | `TryAcquireGlobalCall` failure | the caller retains both provisional slots and calls `ReleaseCall`, which releases connection then global exactly once; its `finally` then releases the pending-admission owner |
| Server drain during local-to-global handoff | final `CurrentState` recheck | pending admission blocks `_callsDrained`; `ReleaseCall` releases local then global, and the `finally` releases pending before the combined predicate can publish |
| Server drain after both acquires | dispatch terminal cleanup | `ReleaseCall` is the sole paired release; the combined predicate can publish only after pending is `0`, global is `0`, and its releasing connection is observed at `0` |
| Dynamic module drain | module/call cancellation state decides response | normal dispatch terminal cleanup still releases capacity; dynamic module lease has its separate owner |

## Invariants and intentional non-unification

`RpcSession.AssertStateInvariant`, `ClientConnection.AssertStateInvariant`,
`ServerConnectionState.AssertStateInvariant`,
`SharpLinkDynamicModule.AssertAccountingInvariant`,
`StreamManager.AssertAccountingInvariant`, and
`SharpLinkServer.AssertCallAccountingInvariant` use only Volatile snapshots and
are called from controlled lifecycle/test boundaries. They take no additional
locks and are not called for each frame, stream item, or selection operation.

For the server, `_callsDrained` has one publication winner. It checks terminal
state, then `pending admissions == 0`, then `global calls == 0`; once terminal
state is visible, a new admission that has not yet incremented pending must fail
its second state check before it can acquire a local slot. The winner records
the global, pending, and releasing-local values with Volatile publication before
completing `_callsDrained`. This makes the drain snapshot an observation of the
actual signal point rather than a later post-release sample.

After that signal, a thread which passed the *first* `Running` check before the
terminal transition can still increment the transient pending counter. Its
second check must then fail before it acquires either a local or global slot.
Consequently `SharpLinkServer.AssertCallAccountingInvariant` treats completed
drain as proof that the global active-call count is zero; a stable test or
diagnostic which also needs `pending == 0` must first join its admission work,
then assert that value explicitly. This avoids turning a harmless no-slot
post-stop entrant into a false invariant failure.

For a stable `RpcSession` snapshot, `Stopping` or `Terminal` additionally
requires a published terminal reason, `StreamManager` termination, and—when a
send pump was created—a requested pump stop. The invariant intentionally checks
the stop *request*, rather than waiting for pump completion, because joining the
pump belongs to disposal and must not turn a transition assertion into a lock or
per-frame wait.

For a stable server-connection `Ready` snapshot, the Session must be negotiated
and accepting calls, and the connection must already have published its default
authentication/call-context snapshot. The authentication identity in that
snapshot may be null when anonymous access is configured; its publication, not
a non-null subject, records that the authentication decision completed.

The following are intentionally retained:

- Logical calls versus physical connection calls: retry and wait-for-ready make
  their lifetimes different; multi-cluster drain needs the former and P2C /
  connection retirement needs the latter.
- Server per-connection versus global calls: they enforce different capacity
  scopes and drain different owners.
- Dynamic module counters: their stripes and lease lifetime gate unregister and
  collectible ALC release; a generic counter would add contention and lose
  ownership information.
- Stream registrations versus dispatcher leases: registry lifetime and active
  decode/dispatch lifetime are separate state domains. `Close` publishes the
  no-new-acquire barrier before detach waits for already-acquired operations.

`RpcSession._activeRequests` was removed because it had no production reader or
writer and therefore no Session-specific protocol semantic. Session draining is
represented by the immutable protocol-phase snapshot; client/server capacity and
pending ownership stay with their actual owners.

## Audit evidence for the removed Session count

The audit was performed from the Phase 13 base
`b44b7358b9b9bccc0694c4d46487980b77604ef5` before deletion:

- The repository-wide references to `_activeRequests`, `ActiveRequestCount`,
  `AddActiveRequest`, and `ReleaseActiveRequest` were the field and three
  members in `RpcSession` plus the lifecycle-only test that called them.
- There was no production caller, reader, diagnostic, metric, flow-control
  path, GoAway/drain path, send-pump path, or protocol admission path that
  observed this value.
- `RpcSession.CanAcceptCalls` and protocol-phase frame validation already
  govern Session-level request admission. Client physical calls, server
  capacity, and logical invocations retain their independently observable
  owners in the matrix above.

Consequently, keeping `_activeRequests` would have left a counter with no
terminal owner or independent semantic. The former test now proves the actual
wire-level Request gate and the Session lifecycle invariant instead of
incrementing an unrelated test-only count.

## Requirement-to-test map

| Requirement / race window | Focused test evidence | Exact terminal assertion |
|---|---|---|
| Logical invocation differs from a physical attempt | `SharpLinkClientRetryTests.LogicalInvocationShouldRemainActiveBetweenRetryAttempts`; `SharpLinkClientCallOptionsTests.WaitForReadyShouldResumeAfterConnectionBecomesReady` | retry backoff has logical count `1` while physical count is `0`; every completed logical call returns to `0` |
| Session drain rejects new requests but keeps existing control/data frames legal | `NegotiatedSessionOptionsTests.DrainingShouldRejectNewRequestsAndPreserveExistingCallFrames` | Request send is `Unavailable`; Response/StreamData/WindowUpdate/Cancel are actually accepted and flushed; Session invariant holds before and after drain |
| Session stopping publishes terminal stream/send-pump state | `NegotiatedSessionOptionsTests.StoppingShouldPublishReceiveTerminationAndStopAnExistingSendPump` | existing pump has a requested stop through the invariant; existing and late receive streams are terminalized and active-stream count is `0` |
| Pending response/error/cancel/deadline/disconnect/GoAway/consumer-abandon/send-failure terminal owner | `RuntimeArchitecturePhase00Tests.PendingTerminalMatrixShouldReleaseThePhysicalOwnerExactlyOnce`; `RuntimeArchitecturePhase00Tests.PendingTerminalRacesShouldLeaveExactlyOnePhysicalOwner`; `RuntimeArchitecturePhase00Tests.FiveWayPendingTerminalRaceShouldChooseOneWinnerAndBalanceEveryCounter`; `PendingRequestTableTests.CancellationShouldNotCompleteOwnerBeforeRegistrationIsPublished` | every named terminal path releases the table slot and owner once; each named path races a competing terminal with one winner; registration-before-cancellation cannot underflow |
| Server connection then global capacity transaction | `SharpLinkServerInvocationTests.ConnectionAndServerCallCapacitiesShouldRejectIndependentlyAndRecover` | global-capacity rejection returns both of its provisional slots exactly once, leaving global count at the two prior owners and the rejected connection at `0` |
| Server Stop versus terminal paired release | `SharpLinkServerInvocationTests.StopAndTerminalReleaseShouldPublishDrainAfterTheConnectionSlotIsReleased` | real `RunAsync`/`StopAsync` remains incomplete while the paired slots are held; the single signal snapshot observes global/pending/releasing-local all at `0` before Stop completes |
| Server Stop during the local-to-global admission gap | `SharpLinkServerInvocationTests.StopShouldWaitForPendingAdmissionBetweenConnectionAndGlobalSlots` (Debug-only deterministic probe) | real `RunAsync`/`StopAsync` observes `pending=1, local=1, global=0` and remains incomplete; resumption returns `Unavailable`, balances all three values, then publishes the all-zero signal snapshot |
| Server admission versus the drain boundary | `SharpLinkServerInvocationTests.CallAdmissionShouldNotCrossTheServerDrainBoundary` | no late admission after drain observes zero; global and connection counters return to `0` |
| Dynamic module acquire versus drain | `DynamicModuleTests.DrainShouldBlockNewLeasesAndWaitUntilEveryConcurrentLeaseIsReleased` | post-drain acquire is unacquired and preserves both aggregates; final lexical leases make both counts `0` |
| Dynamic module unregister and replacement wait for old lease owners | `DynamicRollbackTests.HugeDynamicDrainTimeoutShouldRemainPendingUntilLeaseRelease`; `DynamicRollbackTests.ClientUnregisterRetainedLeaseShouldUseOnlyItsRuntimeContextProvider`; `DynamicRollbackTests.ServerUnregisterRetainedLeaseShouldUseOnlyItsRuntimeContextProvider`; `RuntimeAssemblyIntegrationTests.ReplacementShouldPublishNewRoutesWhileOldUnaryDrainsAndThenReleaseItsAlc` | unregister/replacement remains pending while old ownership exists; each direct module test asserts call/stream counters reach `0`, and the integration replacement releases the old registration/ALC only after its admitted call ends |
| Stream lookup acquired before `CompleteAll` | `StreamManagerTests.CompleteAllShouldCloseLookupBeforeTheLastDispatchLeaseDrains`; `StreamManagerTests.LocalCancellationShouldFlushOnlyAfterAcquiredDispatchesDrain` | business count is `0` while a pre-existing dispatch remains active, a late lookup is blocked after terminal publication, and the final release completes in order |
| Dispatcher detach/reuse barrier | `PooledAsyncStreamDispatcherTests.EarlyDisposeShouldNotPoolWhileProducerIsDecoding`; `PooledAsyncStreamDispatcherTests.AttachedDispatcherShouldNotReturnToPoolWhenPendingCompletionOwnsTheSlot` | no pool reuse before the active dispatch / attached completion releases its lease |

The matrix and test map are intentionally owner-specific: a passing test does
not infer equality between counters with different scopes.

### Terminal and module-lifecycle evidence index

| Exact path | Deterministic / focused evidence | Final accounting evidence |
|---|---|---|
| Response success | `RuntimeArchitecturePhase00Tests.PendingTerminalMatrixShouldReleaseThePhysicalOwnerExactlyOnce` and `PendingTerminalRacesShouldLeaveExactlyOnePhysicalOwner` (`PendingTerminal.Response`) | pending table count `0`; owner registered/completed once; active owner count `0`, including a competing terminal |
| Remote error | same tests (`PendingTerminal.RemoteError`) | same exact table/owner zero assertion, including a competing terminal |
| User cancellation | same tests (`PendingTerminal.UserCancellation`) plus `PendingRequestTableTests.CancellationShouldNotCompleteOwnerBeforeRegistrationIsPublished` | terminal table owner releases once; registration/cancellation barrier cannot underflow |
| Deadline | same tests (`PendingTerminal.Deadline`) | terminal table owner releases once and count reaches `0`, including a competing terminal |
| Disconnect | same tests (`PendingTerminal.ConnectionFailure`) plus Phase 00 five-way race | terminal table owner releases once; concurrent terminal loser cannot retain a slot |
| GoAway | same tests (`PendingTerminal.GoAway`) plus Phase 00 five-way race | terminal table owner releases once; concurrent terminal loser cannot retain a slot |
| Consumer abandonment | same tests (`PendingTerminal.ConsumerAbandonment`) plus `PooledAsyncStreamDispatcherTests.AsyncConsumerAbandonmentShouldJoinTerminalCleanupBeforeDisposeReturns` | pending owner count reaches `0`; stream dispatcher cannot be pooled before terminal cleanup detaches it |
| Send failure | same tests (`PendingTerminal.SendFailure`) | terminal table owner releases once and count reaches `0`, including a competing terminal |
| Client unregister with retained module lease | `DynamicRollbackTests.ClientUnregisterRetainedLeaseShouldUseOnlyItsRuntimeContextProvider` | after the held lease releases, both module aggregates are asserted `0` before the module is released |
| Server unregister with retained module lease | `DynamicRollbackTests.ServerUnregisterRetainedLeaseShouldUseOnlyItsRuntimeContextProvider` | after the held lease releases, both module aggregates are asserted `0` before the module is released |
| Replacement while old server/client ownership drains | `RuntimeAssemblyIntegrationTests.ReplacementShouldPublishNewRoutesWhileOldUnaryDrainsAndThenReleaseItsAlc` | old module stays alive while call is active, then replacement reports release and old ALC unloads after the call finishes |

## Hot-path impact review

The Phase 13 production diff makes no allocation-bearing ownership abstraction:

- `SharpLinkServer.TryAcquireCall` adds a paired `Interlocked.Increment` /
  `Interlocked.Decrement` for the transient pending-admission owner. It has no
  allocation, lock, dictionary lookup, or cross-stripe operation; the normal
  invocation remains covered by the existing per-connection and global counter
  operations. `TryAcquireGlobalCall` remains one increment and comparison.
- On global-capacity rejection the provisional global slot stays with the common
  paired release rather than adding a second rollback path. `ReleaseCall`
  performs local decrement, global decrement, and the cold combined drain
  predicate; its terminal-only winner records diagnostics and completes the TCS.
- The nullable local-to-global probe is compiled only in Debug for the focused
  deterministic UnitTest. Release builds contain neither its field, constructor
  parameter, callback branch, nor test code.
- `RpcSession`, `ClientConnection`, `ServerConnectionState`,
  `SharpLinkDynamicModule`, and `StreamManager` receive only internal
  boundary-check helpers. Production selection, per-frame dispatch, stream
  item, pending-call, and striped-module acquire/release paths do not call
  them. Their failure strings are therefore cold-path only.
- The dynamic module remains a striped counter plus lexical struct lease; the
  StreamManager retains its encoded dispatcher-entry CAS state. Neither gains
  a connection-wide/global lock or a cross-stripe lookup.

This is a source-level allocation/locking audit, not a performance result. The
remote benchmark gate below remains required to detect a JIT or throughput
regression.

## Verification plan

The focused tests exercise exact zero counts after deterministic lifecycle
boundaries, a Debug targeted local-to-global handoff probe, and existing
single-winner pending races. The later serial Ubuntu
gate must include unit, integration, dynamic-module, graceful-draining,
StreamManager, Chaos/AOT as applicable, plus pending/selection/stream-dispatch
allocation and throughput comparisons. No benchmark result is implied by this
document; Phase 13 must retain zero per-call allocation and avoid a new global
lock or dictionary lookup in hot paths.

Benchmark execution is deliberately deferred to the global performance lock.
The candidate comparison must run the exact base SHA and this branch in
alternating warmed-up rounds on the same remote CPU set when reliable affinity
is available; otherwise it must run exclusively with recorded system load.

## Completed remote validation

The final candidate source tree was frozen before the remote gate and is the
source tree committed by this Phase 13 pull request. The comparison baseline
was `b44b7358b9b9bccc0694c4d46487980b77604ef5` (merged Phase 09 `dev`). All
high-load work was serialized with the shared performance lock on Ubuntu
(`.NET SDK 10.0.110`, runtime `10.0.10`, Ryzen 7950X, performance governor).

- Release build completed with `0` warnings and `0` errors; the final Release
  Unit, generator, and integration gates passed `769/769`, `124/124`, and
  `275/275` respectively. The Debug-only local-to-global admission-gap test
  was built and run directly (`1/1`), so its `#if DEBUG` seam is not inferred
  from a Release build.
- Shared-memory NativeAOT smoke passed. The bounded 120-second shared-memory
  Chaos gate completed with `784,977` successful calls, `171,382` injected
  faults, `0` unexpected failures, `0` client/server errors, and `0`
  unobserved exceptions. Long-duration soak testing was explicitly waived.
- BenchmarkDotNet `0.15.8` comparisons used serialized baseline/candidate
  alternation and recorded load, CPU affinity, SDK, and CPU-frequency values
  with each raw run. Pending registration/completion, stream dispatch, and
  admission-disabled paired median changes were respectively `+0.884%`,
  `+0.321%`, and `+0.679%` for mean throughput; their reconstructed workload
  P99 changes were `+0.906%`, `+0.230%`, and `+2.078%`. Allocation and lock
  contention did not regress.
- The sub-nanosecond P2C comparison was re-run because the initial twelve-core
  sample set drifted too much to be a trustworthy gate. The accepted rerun
  pinned every baseline/candidate round to CPU 4 and used five strict
  `baseline -> candidate` pairs. Type-7 P99 values were recomputed from the
  BenchmarkDotNet workload samples (they are iteration-throughput percentiles,
  not end-to-end RPC latency). The paired median was `+0.333%` mean and
  `+0.342%` P99; no allocation or lock-contention change was observed. Two
  zero-sample framework attempts (project lookup and package-restore timeout)
  are retained in the raw artifacts and excluded from this calculation.

Raw reports are retained in this task checkout under
`artifacts/phase13-performance/`; the PR records the exact candidate commit
that those artifacts represent.
