# 07 — Resource and Physical Lifecycle Audit

## 7.1 What was actually observed

| Resource | S evidence | Physical-release conclusion |
|---|---|---|
| Old `ServiceRegistration` | F03/F05/F06/F07 replacement returned `ReferencesReleased=true`, `RemainingCalls=0`; old session replaced | old module references released on the exercised idle Replace paths; F18b covers a held duplex Replace |
| Pinned session snapshot | F18 idle: old session replaced, `retiredServerConnections=0`, old service ALC collected after 30 full GC/finalizer cycles | snapshot is released when the old session closes (idle path) |
| Codec / method facts | no N facts implementation; S did not change codec ownership code | no new evidence; existing codec lifecycle tests were not rerun under S |
| Admission queue / active RPC | F07: `QueuedCalls=1`, `ActivePermits=1`; after rejection both reached 0; F06 active call returned 42 | queue and active call permit returned in the exercised cases |
| Producer / consumer | NOT_RUN | no S evidence |
| Client/server streams and `Dispose` | F18b: a real duplex stream is held across the cut; old session/ALC retained, replacement blocked; after release the stream completes with 4 and the old ALC is collected | held-duplex path has physical-retention/release evidence; other stream tails still NOT_RUN |
| `CancellationTokenSource` | NOT_RUN | no S evidence |
| Session / control frame | F03/F10 old session IDs disappear; F10 2 notifications; F18b old session stays while the stream is active; no frame queue or sender-task counter | session replacement observed, control-frame retention not measured |
| Collectible ALC | F18 idle: `originalAlive=false`; client/server remained live | idle replacement path can collect |
| Remaining references / cleanup counts | `retiredServerConnections=0` in F18 idle; no full reference graph | partial evidence only |

## 7.2 Old Registration retention

The S snapshot is a strong reference to the whole `FrozenDictionary<long, ServiceRegistration>` captured at handshake. This is intentionally stronger than P's live lookup:

- While the session is alive, the old registration cannot be collected, even if the registry no longer advertises it.
- F18 idle shows that once the session is replaced and its connection state is cleared, the old ALC is collectible.
- F18b now covers the held-stream concern for a real duplex stream: the old session remains selectable and the old ALC remains alive while the stream is active; `ReplaceAssemblyAsync` does not complete; after the stream completes and the session retires, the old ALC is collectible. This is direct evidence for the Replace/duplex slice.
- **Still not proven:** the Unregister subcase, server-stream `MoveNext`/`Dispose` tails, producer/consumer CTS tails, and a stream that never completes until forced cancellation.

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

F18b held-duplex event (S, test commit `4f927b40`, canonical `attempt-canonical-4f92`):

```json
{"event":"held-duplex-retains-old-session","payload":{
  "oldSessionId":"8074c325434d49ffa998f254105e5d49",
  "readySessionIds":["8074c325434d49ffa998f254105e5d49"],
  "oldAlive":true,"replaceCompleted":false}}
{"event":"held-duplex-released","payload":{"values":[4]}}
{"event":"held-duplex-collectible-check","payload":{
  "oldSessionId":"8074c325434d49ffa998f254105e5d49",
  "oldAlive":false,
  "readySessionIds":["8150e4388a9a43a7b9dcfbf5c3d73491"],
  "retiredServerConnections":0}}
```

Method: the service was loaded in a collectible `AssemblyLoadContext`; a real generated duplex RPC yielded its first item and was held; Replace was started; the old session remained selectable and the old ALC alive; the replacement was blocked; the stream gate was then released, the second item (4) was consumed, and the stream completed; the old session was replaced and 30 full GC/finalizer cycles collected the old ALC.

F18 idle event (S, `4f927b40`, canonical `attempt-final-4f92`) also shows the old idle session replaced and `originalAlive=false` with `retiredServerConnections=0`.

An earlier F18b `attempt-final-4f92` directory contains two identical runs because the test was executed twice with the same attempt name; it is preserved as `F18b-mixed-attempt` and is not used as canonical evidence.

**Not proven:** the Unregister subcase, server-stream `MoveNext`/`Dispose` blocking, codec/method-fact roots, and client-side old ALC. Do not summarize the whole F18/F16 obligation as passed.

## 7.7 Physical clean-up verdict

`INCONCLUSIVE`. There is now real evidence for idle and held-duplex old-module/ALC release and queue/permit return. There is still no evidence for the Unregister subcase, server-stream `MoveNext`/`Dispose`, producer/consumer CTS tails, codec facts, or control-task physical exit. The S prototype therefore cannot be called fully resource-safe under #775, although the earlier “held stream unknown” gap is narrowed substantially by F18b.
