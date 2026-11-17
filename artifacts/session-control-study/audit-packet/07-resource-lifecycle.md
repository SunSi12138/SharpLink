# 07 — Resource and Physical Lifecycle Audit

## 7.1 What was actually observed

| Resource | S evidence | Physical-release conclusion |
|---|---|---|
| Old `ServiceRegistration` | F03/F05/F06/F07 replacement returned `ReferencesReleased=true`, `RemainingCalls=0`; old session replaced | old module references released on the exercised no-held-stream paths |
| Pinned session snapshot | F18 idle: old session replaced, `retiredServerConnections=0`, old service ALC collected after 30 full GC/finalizer cycles | snapshot is released when the old session closes (idle path) |
| Codec / method facts | no N facts implementation; S did not change codec ownership code | no new evidence; existing codec lifecycle tests were not rerun under S |
| Admission queue / active RPC | F07: `QueuedCalls=1`, `ActivePermits=1`; after rejection both reached 0; F06 active call returned 42 | queue and active call permit returned in the exercised cases |
| Producer / consumer | NOT_RUN | no S evidence |
| Client/server streams and `Dispose` | NOT_RUN (F18 held-stream subcase not run) | no S evidence |
| `CancellationTokenSource` | NOT_RUN | no S evidence |
| Session / control frame | F03/F10 old session IDs disappear; F10 2 notifications; no frame queue or sender-task counter | session replacement observed, control-frame retention not measured |
| Collectible ALC | F18 idle: `originalAlive=false`; client/server remained live | idle replacement path can collect |
| Remaining references / cleanup counts | `retiredServerConnections=0` in F18 idle; no full reference graph | partial evidence only |

## 7.2 Old Registration retention

The S snapshot is a strong reference to the whole `FrozenDictionary<long, ServiceRegistration>` captured at handshake. This is intentionally stronger than P's live lookup:

- While the session is alive, the old registration cannot be collected, even if the registry no longer advertises it.
- F18 idle shows that once the session is replaced and its connection state is cleared, the old ALC is collectible.
- **Not proven:** a long-running old stream keeps the old session alive until the stream physically exits. The client `TryCommitSessionRefreshRetirement` only removes the source after `CallAdmissionReservationCount == 0 && ActiveCallCount == 0`; a stream that keeps an active call will therefore keep the pinned snapshot and old ALC alive. This is exactly the #998 F18 “held stream” concern, and it was not executed.

## 7.3 Codec, Facts, and method tables

S does not implement N0 method facts and does not change codec ownership. The only change is which `ServiceRegistration` the dispatch path selects. Therefore:

- There is no S-specific evidence for “same codec/facts across queue, decode, interceptor, mapper, stream tail”.
- The existing `QueuedSelection` and codec-publication tests are P evidence, not S evidence. Some of them fail on S because the session lacks a dynamically registered contract.
- No claim is made that S preserves the full #732/N0 facts obligations.

## 7.4 Queue and active-call ownership

F07 uses a **test-only external admission holder**, not a mock of the RPC path. It acquires the real admission permit and queues a real TCP RPC. After the cut:

- the queued request becomes `Unavailable`;
- no R1 or R2 user code runs;
- `QueuedCalls` and `ActivePermits` return to 0.

F06 starts a real `BlockAsync` call that has already entered R1 user code, cuts, then releases the block; the result is 42. This demonstrates logical completion of an admitted call. It does **not** observe the final codec/CTS/stream owner release after the response.

## 7.5 Control-frame retention

`RetirePinnedSessionsAsync` is a tracked framework task. The test does not capture:

- the number of control-frame writer leases after send;
- whether the control task exited before `Stop`;
- queue bytes held by the control frame;
- behavior when the send queue is full.

The source sends one frame and does not retain a per-session retry record. There is no unbounded ID table, but also no proof of bounded recovery after a lost frame.

## 7.6 ALC evidence detail

F18 idle event (S, `525a5aed`):

```json
{"event":"collectible-check","payload":{
  "oldSessionId":"0c92569d3372479687790a81c1441b6c",
  "newSessionIds":["e1340bc53e874726aaec291f38a8a27b"],
  "originalAlive":false,
  "retiredServerConnections":0}}
```

Method: the service was loaded in a collectible `AssemblyLoadContext`, the original service was replaced, the old session was replaced, then 30 full `GC.Collect`/`WaitForPendingFinalizers` cycles were run while the client and server stayed alive. A first attempt failed because the test method kept the old `Assembly` and `AssemblyLoadContext` locals alive; that attempt is preserved and is not counted as product evidence.

**Not proven:** the held-stream enumerator case, codec/method-fact roots, and client-side old ALC. Do not summarize this as “F18 passes”.

## 7.7 Physical clean-up verdict

`INCONCLUSIVE`. There is real evidence for idle old-module/ALC release and queue/permit return, but no evidence for the held-stream, producer/consumer, CTS, codec facts, output `MoveNext`/`Dispose`, or control-task physical exit obligations. The S prototype therefore cannot be called resource-safe under #775.
