# Runtime Architecture Phase 14: dispatcher detach completion

Issue #82 replaces the client consumer-abandon slow-path `Task.Yield` loop with a
one-shot lifecycle event owned by the `StreamManager` entry. The change is internal:
it does not add or change a public API, public ABI, protocol field, or generated
contract surface.

## Two deliberately separate terminal signals

| Signal | Owner | It means | Consumer |
|---|---|---|---|
| `WaitForDetachedAsync` | `StreamManager.DispatcherEntry` | the entry has left StreamManager ownership after its stream-completion callback | Client consumer-abandon cleanup may send its late Cancel after this boundary |
| `WaitForDispatchesAsync` / `OnDispatchesDrained` | the same entry and its dispatcher lease | every dispatch acquired before `Close` has released | pooled dispatcher reuse and local cancellation cleanup |

The events are not interchangeable. An entry can detach while an already-acquired
dispatch still executes. In that state the client may observe detach, but the
dispatcher lease must not return to its pool until the final `Release` invokes
`OnDispatchesDrained`.

## Publication and ordering

`DispatcherEntry` keeps one nullable `DispatcherEntryCompletions` reference—the
same common-entry reference count as the pre-Phase-14 dispatch-drained wait. The
holder is created only by the first wait path and independently lazily creates the
two `RunContinuationsAsynchronously` completion sources. Its signal bits, interlocked
publication, and second state checks prevent a waiter racing `Detach` or the final
dispatch release from installing an unsignalled completion source. The physical
storage is shared; the two completion semantics are not.

The existing StreamManager ordering remains intact:

```text
remove entry / Close
  -> clear receive-consumption callback
  -> RpcSession.OnReceiveStreamCompleted
       -> flush final consumed credit
       -> enqueue WindowUpdate in the session SendPump
  -> DispatcherEntry.Detach
       -> publish detach completion
  -> ClientConnection may enqueue ConsumerAbandoned Cancel in that same SendPump
```

Consequently observing detach proves that final receive-credit enqueue has already
happened. `Detach` was intentionally not moved ahead of the stream-completion
callback merely to wake a waiter earlier.

## Client lifetime behavior

When consumer abandonment loses the pending-call terminal race, `ClientConnection`
asks `StreamManager` to unregister any still-published entry and waits for the
entry's detach event using the internal `RpcSession.LifetimeToken`. A disconnect or
session shutdown cancels that framework-owned token, ends the wait, and sends no
new Cancel. If detach wins while the session remains connected, the client makes
one existing bounded `TrySendCancel` attempt. The terminal race therefore has these
outcomes:

| Winner | Result |
|---|---|
| detach while connected | final credit is already enqueued; enqueue one ConsumerAbandoned Cancel |
| session terminal transition | cancellation ends the wait; do not enqueue Cancel |
| concurrent detach/disconnect | at most one bounded Cancel attempt; existing connection-closed handling remains the fallback |

The token is captured in the Session constructor, rather than read from its source
during teardown, so the internal wait uses a stable framework-lifetime value.

## Focused evidence

| Requirement | Focused test evidence |
|---|---|
| detach-before-wait, waiter registration race, multi-waiter, cancellation, and cancellation/detach race have no lost wake-up | `StreamManagerTests.DetachBeforeWaitShouldCompleteSynchronouslyWithoutLostWakeup`, `DetachWaitShouldCompleteEveryRegisteredWaiterOnce`, `DetachRacingWaiterRegistrationShouldNotLoseWakeup`, `DetachWaitCancellationShouldNotPreventLaterDetach`, and `DetachAndCancellationRaceShouldNeverLoseWakeupOrDoubleSignal` |
| final receive credit precedes observable detach | `StreamManagerTests.DetachCompletionShouldFollowTheFinalCreditCallback` |
| detach does not collapse the active-dispatch/pool-return barrier, including when both waits share the holder | `StreamManagerTests.DetachShouldNotReturnAnActiveDispatcherLeaseBeforeItsLastRelease`, `DispatchDrainAndDetachWaitsShouldRemainIndependentWhenSharingCompletions`, `PooledAsyncStreamDispatcherTests.AsyncConsumerAbandonmentShouldJoinTerminalCleanupBeforeDisposeReturns`, and `DelayedOldPoolReturnShouldNotReturnOrClearReusedLease` |
| remote terminal completion versus consumer abandonment settles the pending slot once and enqueues WindowUpdate before Cancel | `ClientConnectionConsumerAbandonmentTests.ConsumerAbandonmentShouldEnqueueFinalCreditBeforeCancelAfterDetach` |
| disconnect cancels the detach wait and emits no Cancel | `ClientConnectionConsumerAbandonmentTests.SessionDisconnectShouldCancelDetachWaitWithoutSendingCancel` |

The local focused Debug gate passed those tests. It also ran three reversible
pseudo-mutations: removing the detach signal caused the waiter test to time out;
moving detach before final credit failed the ordering assertion; restoring the old
client polling loop failed the disconnect wait-entry assertion. Each mutation was
immediately reverted.

## Hot-path and remote gate

Normal stream registration, dispatch, completion, and detach with no waiter do not
create the shared completion holder. A consumer-abandon waiter creates at most one
holder and one detach completion source for an entry; it does not allocate one per
item or frame. This preserves the pre-Phase-14 common `DispatcherEntry` object size
instead of adding a second reference to every normal stream lifecycle. The only
changed polling path is the cold ClientConnection abandon slow path; existing
pooled-dispatcher mechanics remain unchanged.

The serialized remote performance gate remains required before merge: compare the
base and candidate normal streaming allocation/throughput and an abandon-plus-
delayed-detach continuation scenario under the global performance lock. No remote,
Release, AOT, Chaos, stress, benchmark, or full-suite result is claimed by this
document.
