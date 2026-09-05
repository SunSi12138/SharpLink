# Logical call and child-stream ownership

Issue #399 narrows SharpLink's stream lifetime model around one rule:

> A logical RPC call owns its terminal decision; inbound stream routes are stable child mailboxes of
> that call/request identity, and typed consumers are temporary children of those mailboxes rather
> than replacement route owners.

The wire protocol remains unchanged: `requestId` identifies the logical call and `streamId`
identifies one child stream within that call.

## Characterization before this change

| Concern | Previous owner / authority | Consequence |
| --- | --- | --- |
| Client call terminal | `PendingCall` slot CAS in `PendingRequestTable` | Response, cancellation, deadline, GoAway/disconnect and response-stream abandonment already converge on one request terminal authority. |
| Server call terminal | `ServerCallCancellationState` when the call requires cancellation/deadline/module/admission tracking; otherwise the synchronous dispatch path uses the request lifetime directly | Streaming paths that materialize a state use its terminal gate; unary can retain the compact no-state specialization. |
| Request -> child-stream routing | `StreamManager.RequestDispatchers` -> `DispatcherEntry` | One stable `(requestId, streamId)` lookup exists and the outer entry owns route dispatch acquisition/detach. |
| Deferred inbound stream bytes | `PreAdmissionStreamDispatcher` | Frames can arrive before generated typed consumption is attached. |
| Typed inbound child lifetime | `InboundStreamChildDispatchState` created during attachment | A second dispatch-state authority was introduced only because ownership moved from deferred route to typed dispatcher. |
| Attachment ownership | `_attachingDispatcher`, `_attachingDispatchState`, `_attachmentBarrier` and replay claim state | Live ingress, replay, abandonment and terminal publication had to coordinate an owner handoff. |
| Retention phase change | `PromoteFrom` + `PreAdmissionStreamLeaseRetention` permit migration | Already-buffered owners were re-reserved under a new policy, temporarily creating two accounting domains and rollback branches. |
| Typed consumer pooling | `PooledAsyncStreamDispatcher<T>` intrinsic lease generation plus the extra child dispatch state | Pool safety depended on both its own producer/consumer generation and a separate attachment-created state object. |

The server receive path therefore had the effective chain:

```text
requestId -> RequestDispatchers
          -> DispatcherEntry
          -> PreAdmissionStreamDispatcher
          -> attaching typed dispatcher
          -> InboundStreamChildDispatchState
          -> PooledAsyncStreamDispatcher<T>
```

The problem was not `StreamManager` itself; it was the ownership-transfer machinery below its
stable request/stream lookup.

## Model after this change

The route remains the same object from the first deferred frame until request/peer cleanup:

```text
requestId -> RequestDispatchers
          -> DispatcherEntry
          -> stable PreAdmissionStreamDispatcher mailbox
                    |
                    +-- optional typed consumer child
```

`PreAdmissionStreamDispatcher` now implements `IStreamDispatchState` directly for its typed child.
There is no `InboundStreamChildDispatchState` allocation and no child route to replace. Closing a
typed consumer closes only the mailbox's consumption window; the mailbox itself remains installed
so late peer frames are discarded without recreating a child or targeting a pooled object.

`StreamManager` still invokes attachment in two method calls, but this is no longer an ownership
protocol. `TryBeginAttach` performs state-only publication while the per-request registry lock is
held; `FinishAttach` runs callbacks/decode replay after that lock is released. There is no
attachment `TaskCompletionSource`, second attaching owner, or barrier object.

### Authority count

For an attached server inbound child stream:

- before: outer `DispatcherEntry` route state + deferred-route attachment state +
  `InboundStreamChildDispatchState` + pooled dispatcher lease generation;
- after: outer `DispatcherEntry` route state + stable mailbox child-dispatch state + pooled
  dispatcher lease generation.

More importantly, there is now **zero route-owner handoff**. The mailbox is never replaced by the
typed consumer and never transfers the physical buffered resource to a replacement route owner.

## Stable-mailbox invariants

1. `(requestId, streamId)` resolves to one stable mailbox identity for the lifetime of the inbound
   child stream.
2. Typed attachment changes only mailbox consumption state. Live frames that race replay remain in
   the same mailbox queue and share the same 4096-element limit.
3. The mailbox directly tracks child dispatches. A typed consumer may close/dispose while frames are
   in flight; it is detached from the mailbox only after those already-acquired child dispatches
   and any active replay owner drain.
4. `Close` and `Detach` are separate facts. Consumer terminal closes new child delivery; parent/call
   cleanup later publishes detach, which is the point at which a pooled child may be reused.
5. Closing the typed consumer does not unregister the mailbox. Late `StreamData` is consumed as
   discard/credit cleanup and cannot recreate or rebind the typed child.
6. Peer terminal is forwarded to the typed child at most once. OneWay/local completion may keep the
   stable mailbox registered as a discard sink until the call releases it.
7. Request cleanup still closes the outer `DispatcherEntry` first and waits its already-acquired
   route dispatches before generation/codec resources can be reclaimed.

## Retention ownership and hard bounds

Retention configuration changes in place instead of moving buffered items to a replacement
retention owner.

Each `BufferedItem` keeps the exact external release callback that admitted it. The mailbox also
owns one stable retained-byte count across lifecycle phases. When a call moves from admission
retention to active/pre-invocation retention:

- already-buffered items keep their original external accounting owner until replay/discard;
- future frames use the new external policy;
- the mailbox count already includes both old and new items, so a smaller active no-flow-control
  byte limit applies to the whole stable mailbox immediately;
- if existing retained bytes already exceed that active limit, reconfiguration marks the mailbox
  terminal and releases the buffered owners once; it does not reserve them again under another
  policy;
- there is no temporary double reservation, rollback loop, or release-callback rewrite.

Server-wide pre-admission accounting now passes the governor reserve/release callbacks directly to
the mailbox, and the no-flow-control active byte cap is enforced by the mailbox itself. The runtime
therefore no longer needs `PreAdmissionStreamLeaseRetention`, its StreamManager permit adapter, or
the separate `ActivePreInvocationStreamRetention` counter.

## Call-wide terminal ownership

### Client

`PendingRequestTable` remains the authoritative client call terminal owner. `PendingCall` slot
removal is a single CAS boundary for response, remote error/stream complete, caller cancellation,
deadline, consumer abandonment, send failure, connection close and GoAway. Response-stream
cleanup is subordinate to that terminal decision and late frames cannot republish a removed slot.

### Server

`ServerCallCancellationState` remains the authoritative server terminal gate whenever a call state
is materialized. Admission/decode/cancel/deadline/module/connection transitions publish through
that state before request-owned resources are released. The compact no-state specialization is
retained for ordinary unary work so #399 does not add a permanent unary allocation or request-path
lookup solely for structural uniformity.

Inbound stream mailboxes are request children, not terminal authorities: they publish stream peer
terminal/credit bookkeeping beneath that call lifetime, but cannot change the logical call's
winning terminal cause.

## Removed transfer machinery

This change removes or collapses:

- `InboundStreamChildDispatchState`;
- `_attachingDispatcher` / `_attachingDispatchState` dual-owner state;
- the attachment `TaskCompletionSource` barrier;
- dispatcher-to-dispatcher ownership promotion;
- buffered retention re-reservation/rewrite during phase changes;
- `PreAdmissionStreamLeaseRetention` and its StreamManager permit-adapter extension;
- the separate active pre-invocation retention counter.

The replay loop remains because frames may physically arrive before the typed codec is available.
It no longer coordinates an owner handoff: while replay is active, new frames append to the same
mailbox, parent detach is remembered as one mailbox-state bit, and typed detach occurs after replay
and already-acquired child dispatches drain.

## Validation map

Deterministic coverage is provided by existing call/stream race suites plus mailbox-specific tests:

- deferred capacity and replay/live ingress share one 4096-element budget;
- the stable byte count spans admission and active phases without re-reserving existing owners;
- reconfiguration rejects an already-over-budget mailbox without migration;
- the typed child binds directly to the stable mailbox dispatch state;
- attachment callbacks remain outside the request registry lock;
- inbound abandonment, peer-terminal late data, completion exceptions, request drain and
  pre-credit streaming lifecycle tests cover early break, late frames, cleanup ordering and credit;
- client cancellation/deadline/GoAway tests continue to exercise the `PendingCall` terminal CAS;
- server cancellation/admission/stop tests continue to exercise `ServerCallCancellationState`.

Performance evidence for the exact `dev` baseline and exact final head is produced with the same
existing performance-matrix script. The comparison includes Unary, ServerStreaming,
ClientStreaming and DuplexStreaming. The unary implementation is not modified by the mailbox
change and receives no new stream lookup, lock, allocation or virtual dispatch.
