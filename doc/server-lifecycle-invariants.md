# SharpLinkServer lifecycle and ownership invariants

This note characterizes the current `dev` behavior of `SharpLinkServer` before further structural extraction under #344. It is descriptive: behavior changes belong in separate issues. The current implementation has already split several responsibilities into collaborators such as `FrameworkTaskSupervisor`, `ServerConnectionAdmission`, and `ServerShutdownPlan`; the invariants below describe the ownership boundaries those collaborators must continue to preserve.

## Server state and shared stop ownership

`SharpLinkServer` has the internal states `Created`, `Starting`, `Running`, `Draining`, `Stopped`, and `Faulted`.

- `RunAsync` establishes at most one `_runTask`. Later calls reuse that task; only a terminal server that never established `_runTask` rejects a first run with `ConnectionClosed`.
- Cancellation passed to the `RunAsync(cancellationToken)` call that first establishes `_runTask` is server-lifecycle input, not merely cancellation of that caller's wait. If that token cancels while the run loop is still `Running`, `RunCoreAsync` establishes or reuses the shared `_stopTask` with `StopCoreAsync(TimeSpan.Zero)` and waits it. If this path establishes `_stopTask` first, later `StopAsync` or run-failure cleanup reuses that zero-grace stop operation.
- Current implementation limitation: once `_runTask` exists, later `RunAsync(cancellationToken)` calls reuse it without wiring their tokens into `RunCoreAsync`, so cancellation from those later callers does not currently request shutdown. This is a known implementation/public-contract mismatch rather than a durable invariant: the public `ISharpLinkServer.RunAsync` cancellation-token contract says the supplied token requests immediate shutdown when canceled and does not restrict that promise to the first caller. Subsequent extraction must not treat the first-caller-only quirk as behavior that must be preserved; aligning implementation with the public contract is a separate behavior decision.
- Normal `StopAsync` also establishes `_stopTask` only when no shared stop operation exists. Its own caller cancellation token is different from the run-lifecycle token above: it cancels only that `StopAsync` caller's wait and does not replace or cancel the shared cleanup operation. This remains true when the caller token is already canceled before `StopAsync` is entered: the method first establishes or reuses `_stopTask`, then applies cancellation to that caller's wait.
- The first normal stop path that establishes `_stopTask` owns the graceful timeout for the entire shared cleanup operation. Later `StopAsync` calls reuse the same task, and their `gracefulTimeout` arguments do not shorten, extend, or otherwise reconfigure the established graceful deadline. First-run cancellation is the same ownership rule with an implicitly fixed `TimeSpan.Zero` grace when it establishes `_stopTask` first.
- Normal stop publishes `Draining` before it stops admission, seals framework-task ownership, cancels accept, disposes the listener, and sends `GoAway`.
- An unexpected run-loop failure publishes `Faulted` while holding `_stateGate`, then establishes or reuses the same `_stopTask`. If a normal stop or first-run cancellation already owns `_stopTask`, the failure path waits that operation rather than starting independent cleanup.
- Cleanup-task ownership and terminal-state publication are separate concerns. The final observable `Stopped`/`Faulted` value can depend on the ordering of the stop terminal write and a racing run-failure `Faulted` write. No characterization test relies on a transport that ignores its `AcceptAsync` cancellation token to manufacture an otherwise unsupported ordering.
- `HealthStatus` is `Ready` only in `Running`, `Draining` only in `Draining`, and `Unhealthy` otherwise.

The durable restart invariant is that a terminal server never creates a second run loop.

## Stop ordering and call-drain ownership

The normal stop path currently performs these ownership transitions:

1. Publish `Draining` under the registry gate.
2. Stop admission-controller intake and begin dynamic-module drain.
3. Seal `FrameworkTaskSupervisor`, preventing shutdown from acquiring open-ended new framework ownership.
4. Cancel accept, start listener disposal, and send `GoAway` to current connections.
5. Attempt to publish server call drain, then wait up to the graceful deadline when drain is not yet complete.
6. If drain completed, flush sessions. If drain did not complete, retain the server service graph through deferred cleanup.
7. Cancel the force-stop token, close sessions, and drain the sealed framework-task supervisor within the bounded cleanup budget.
8. Dispose server-owned services/resources immediately only when call drain completed; otherwise deferred service cleanup waits for the drain signal.
9. Publish `Stopped` on successful bounded cleanup or `Faulted` when bounded cleanup fails/times out.

A non-cooperative user invocation may therefore outlive `StopAsync`. Transport/framework teardown is bounded; the service graph required by outstanding ownership is not disposed merely because the transport stop operation has returned.

## Pending admission is drain ownership

The current server call-admission path has an explicit `_pendingCallAdmissions` ownership counter in addition to connection-local and server-global active-call counters.

`TryAcquireCall` follows this ordering:

- Require server `Running` before admission begins.
- Increment pending-admission ownership.
- Re-check `Running`; a stop that won before this check rejects without acquiring a local slot.
- Acquire the connection-local call slot.
- Acquire the server-global call slot.
- Re-check `Running`; if drain won after provisional acquisition, release the local/global ownership and return `Unavailable`.
- Release pending-admission ownership in `finally`, regardless of the result.

The call-drain publication winner proceeds only after it observes pending admissions and global active calls at zero. When release participates in publication, it also requires the releasing connection's local active-call count to have reached zero. `LastCallDrainSignalForDiagnostics` records those zero-valued observations made by the single publication winner.

A completed drain signal is not, by itself, a durable assertion that the live pending-admission counter is currently zero. A thread may have observed `Running` before stop, then increment `_pendingCallAdmissions` after the winner's zero reads; its second server-state check prevents it from acquiring any local or global call slot. Consequently completed drain proves that active-call ownership cannot appear after the boundary, while a caller that needs the stronger stable condition `pending == 0` must also join the competing admission work before checking it.

The local/global release order is deliberate: connection-local ownership is released before the global counter is decremented, so drain cannot become observable while that connection still reports the call as active.

## Request-ID admission is weaker than call ownership

`TryAcceptRequest` requires the server to remain `Running` and the connection to remain `Ready`, but request-ID publication has different rollback semantics from active-call acquisition.

`ServerConnectionState.TryRecordAcceptedRequest` performs a `Ready` check, writes `_lastAcceptedRequestId`, and checks `Ready` again. It does not compare the new ID with the previous value, use CAS, or enforce monotonic/duplicate rejection. If drain wins after the write, the method can return `false` while the attempted `LastAcceptedRequestId` remains observable; shutdown `GoAway` can therefore observe that value.

No stronger request-watermark invariant is characterized here.

## Connection lifecycle and handshake publication

A `ServerConnectionState` moves monotonically through `Handshaking`, `Ready`, `Draining`, and `Closed`.

- `MarkReady` writes authentication/default-call-context references before its `Handshaking -> Ready` CAS. Those references may therefore be transiently visible during a drain race.
- If drain wins the CAS, `MarkReady` rolls those references back before returning `false`.
- If ready wins first, a later `MarkDraining` advances `Ready -> Draining`; lifecycle must never regress to `Ready`.
- `MarkDraining` prevents new request/call acquisition and completes the connection-local drain signal immediately when no active calls remain.
- `CloseAsync` cancels connection work, begins session shutdown, waits for any owned `PipeReader` result to be released, disposes the session, publishes `Closed`, and starts service cleanup.

## Connection-service ownership after transport close

Transport/session closure does not release connection-scoped service ownership held by an active call.

`ServiceCleanupTask` waits for the connection active-call count to reach zero before it disposes connection-scoped services, their `IServiceScope`, the deadline scheduler, and the connection cancellation source. A non-cooperative call can therefore safely retain its connection service graph after the session has already reached `Closed`.

Retired-connection cleanup has two server-level paths on current `dev`:

- If retirement observes `ActiveCalls == 0`, `DisconnectConnectionAsync` / `RetireConnectionAsync` directly await `CompleteRetiredConnectionCleanupAsync`. During normal server stop this work is part of session close, so the existing `ServiceLifetimeIntegrationTests.ServerStopShouldJoinConnectionServiceCleanup` requires `StopAsync` to remain joined to a zero-active-call connection-service disposal.
- If retirement observes an active call, the server increments `_deferredConnectionCleanups` and observes `ServiceCleanupTask` asynchronously. A zero-grace stop may finish while this cleanup still waits for the call or for service disposal; the deferred observer remains responsible for exactly-once completion and removing the connection from `_retiredConnections` in cleanup's `finally` path.

This conditional split is part of the ownership baseline: zero-active cleanup is joined synchronously by retirement, while active-call cleanup is deliberately detached without releasing the service graph or retired-registry ownership early.

## Server service graph and dynamic modules

Server-level cleanup releases dynamic modules before disposing initial service registrations and the server-owned provider, admission controller, and runtime context. Cleanup aggregates failures so one failing owner does not skip later owners.

Dynamic-module removal preserves the same retention rule as connection cleanup: route/publication state can be removed before all user work completes, but registrations, generated-manifest ownership, and service instances remain retained until their drain ownership is released. Failed registration/unregistration paths must release only resources they actually own and must not publish partially constructed snapshots.

## Characterization coverage

The executable baseline is spread across existing tests and the focused tests added for #366:

- `ServerStopOwnershipCharacterizationTests.FirstRunCancellationShouldOwnZeroGraceSharedStopWithoutExplicitStop` verifies cancellation of the token on the `RunAsync` call that establishes the run task drives normal zero-grace shutdown while active-call ownership may remain, and that a later explicit stop reuses that already-established cleanup rather than applying a new grace period. It characterizes the current first-run path without converting the later-caller token limitation into a durable contract.
- `ServerStopOwnershipCharacterizationTests.StopCallerCancellationShouldOnlyCancelThatCallerWait` verifies cancellation after a long-grace `StopAsync` has established shared cleanup only cancels that caller's wait; the shared stop remains in `Draining`, later callers join it, and it reaches `Stopped` after active-call ownership drains.
- `ServerStopOwnershipCharacterizationTests.PreCancelledStopCallerShouldStillStartSharedCleanup` verifies a caller token canceled before entry still does not short-circuit shutdown establishment: `StopAsync` enters `Draining` and creates/reuses shared cleanup before the caller observes cancellation, and an uncancelled later caller joins the same task.
- `ServerStopOwnershipCharacterizationTests.FirstStopOwnerShouldOwnSharedGraceTimeout` covers both timeout precedence directions with an active call: a later zero-grace caller cannot shorten a long-grace first owner, and a later long-grace caller cannot extend a zero-grace first owner; both callers observe the same shared stop task.
- `ServerLifecycleOwnershipCharacterizationTests.ReadyPublicationShouldNotCrossConcurrentDrainBoundary` covers the late-handshake/drain race with fresh connection state and separately fixes drain-first and ready-first linearizations.
- `ServerLifecycleOwnershipCharacterizationTests.ConnectionServicesShouldRemainOwnedUntilActiveCallsDrain` verifies both the connection service object and its `IServiceScope` remain alive through transport close until the last active call releases ownership.
- `ServerLifecycleOwnershipCharacterizationTests.CallAdmissionShouldNotCrossServerDrainBoundaryWithFreshLifecycleState` races real `RunAsync`/`StopAsync` lifecycle transitions on fresh servers, verifies the publication winner's observed zero pending/global/local snapshot, and verifies final live-counter convergence after competing admission work has joined; it separately covers admission-first and stop-first behavior without rewinding one-shot drain state.
- `ServerLifecycleOwnershipCharacterizationTests.DeferredRetiredConnectionCleanupMayOutliveServerStopWhenCallOutlivesGrace` fixes the active-call deferred-retirement path and verifies its server observer remains owned until cleanup finishes exactly once and releases the retired-registry entry.
- `ServiceLifetimeIntegrationTests.ServerStopShouldJoinConnectionServiceCleanup` covers the complementary zero-active retirement path where server stop joins connection-service disposal.
- `ServerConnectionStateTests.CloseShouldWaitForSessionLoopToReleaseItsReadBuffer` covers session-loop / `PipeReader` ownership during close.
- `ServerConnectionStateTests.ConnectionServiceCleanupShouldSurfaceEveryFailure` covers connection-service cleanup aggregation.
- `SharpLinkServerInvocationTests.ConnectionAndServerCallCapacitiesShouldRejectIndependentlyAndRecover` covers independent connection/server capacity ownership and rollback.

These tests and this note are the baseline for subsequent structural work. Responsibilities may move between collaborators, but admission, drain, transport, service, and cleanup ownership must not silently cross these boundaries.
