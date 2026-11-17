# 05 — Concurrency and Linearization Timeline

All timestamps below are **same-process monotonic milliseconds** from the test recorder (`Stopwatch`). Client and server run in the same process in these integration tests, so the monotonic values are comparable within a case. They are not wall-clock times and are not used across machines.

The recorder does **not** currently emit a client-side “SessionRefreshRequested received” event. For S, the appearance of a new Ready session is observed, but the exact frame-receipt instant is not. Those cells are marked `UNOBSERVED`; they must not be inferred from the new-session timestamp.

## F04 — old client with changed contract hash

Canonical evidence: `evidence/s-core/F04/events.jsonl` (test commit `948fb44d`, attempt `attempt-final-948f`).

| Order | Event | Time (ms) | Observed fact |
|---:|---|---:|---|
| 1 | old proxy call before replacement | 477.655 | returns from R1; old hash `a3054d…` |
| 2 | contract replaced with changed hash, new service registered | before 2494.399 | server accepts replacement |
| 3 | replacement session check | 2494.399 | no new Ready session in 2 s; old session still listed |
| 4 | old proxy call after replacement | 2500.979 | `Unavailable` |
| 5 | R2 service count | 2503.136 | `newServiceCreated=0` |

This is fail-closed: the old client does not enter R2. It is also an availability finding: the old client does not converge to a new session because its local generated contract is incompatible. The first F04 attempt waited 10 s for a replacement session and timed out; that log is preserved in `evidence/s-core/failures/F04-attempt-1-overstrict-session-wait.log`.

## F05 — stale request after the server cut, notification held

Canonical evidence: `evidence/s-core/F05/events.jsonl` (S, `525a5aed`).

| Order | Event | Time (ms) | Observed fact | Evidence quality |
|---:|---|---:|---|---|
| 1 | S Replace prepares candidate | before 200.671 | client still on old session; R1 Created=0 | observed |
| 2 | old module `TryBeginDraining` (cut) | before 212.130 | state CAS to Draining; new R not yet visible to old session | source-level, not separately timestamped |
| 3 | new `_services` snapshot published | before 212.130 | Replace commit completed; test hook fires after publish | observed as `server-cut-committed` at 212.130 |
| 4 | retirement notification enqueued | after 212.130, held | `PinnedSessionRetirementBarrierForTesting` holds the task before send | observed as held; exact enqueue timestamp not emitted |
| 5 | client receives notification | `UNOBSERVED` | no client frame-receipt event | evidence gap |
| 6 | stale request sent | ~233.389 | request rejected with `Unavailable`; R1 Created=0, R2 Created=0 | observed |
| 7 | business start claim | none | no R1/R2 user code entered | observed via counters |
| 8 | notification released; replacement session Ready | 316.481 | new session ID differs | observed |
| 9 | fresh request | 334.015 | returns 11 from R2 | observed |

**Linearization argument:** the `TryBeginDraining` CAS is ordered before the new snapshot write. The stale request read the old session's pinned table, found the old registration, and `TryAcquire`/`AcceptsCalls` observed `Draining`; it therefore could not obtain a business start claim. The test did not rely on a sleep: the server's cut hook and the held notification barrier define the window.

**What is not proven:** the exact client receive time and the exact server send time are not recorded; the test proves “after cut, before notification release” from the server side, not a client-side happens-before edge.

## F07 — queued request loses to cut

Canonical evidence: `evidence/s-core/F07/events.jsonl`.

| Order | Event | Time (ms) | Observed fact |
|---:|---|---:|---|
| 1 | admission holder owns only permit | before 234.503 | external holder `ActivePermits=1` |
| 2 | R1 request queued | 234.503 | `QueuedCalls=1`, R1/R2 Created=0 |
| 3 | cut observed | 247.286 | queue still 1; R1/R2 Created=0 |
| 4 | permit released | after 247.286 | queued continuation resumes |
| 5 | queued request terminal | 256.326 | `Unavailable`, no R1/R2 business |

**Linearization argument:** the queued selection is the old `ServiceRegistration`; on resume `DispatchAdmittedRpc` checks `AcceptsCalls`, which is false after the cut. It does not re-resolve the live registry, so it cannot enter R2. If the permit had been granted before the cut, the request would have been in F06's “start claim won” class; the test deliberately releases the permit only after the cut.

## F09 — unrelated service collateral

Canonical evidence: `evidence/s-core/F09/events.jsonl`.

| Order | Event | Time (ms) | Observed fact |
|---:|---|---:|---|
| 1 | unrelated B call before replacement | 222.753 | returns 7 on old session |
| 2 | A replacement and session retirement | 264.914 | old session ID disappears; new session ID appears; `foreignServiceInterrupted=true` |
| 3 | unrelated B call after replacement | 286.657 | returns 7 on the new session |

This records S's declared **whole-session collateral cost**: B remains functionally correct, but its physical session and all in-flight/pending work are moved. It does not prove that B had no request failures during the gap; that would require a continuous request loop with a receipt timeline, which was not implemented.

## F10 — two independent sessions

Canonical evidence: `evidence/s-core/F10/events.jsonl`.

| Order | Event | Time (ms) | Observed fact |
|---:|---|---:|---|
| 1 | two physical sessions ready | before 230.733 | two distinct session IDs |
| 2 | replace + retirement | 230.733 | 2 notifications; two new distinct session IDs; no old ID reused |
| 3 | fresh calls | after 230.733 | test asserted both R2 calls returned 2 and 11 |

The test does not emit a per-call event for the two fresh calls, only the PASS result; this is an evidence granularity gap, not a substitute for a timeline.

## F06 — admitted old call survives cut

Canonical evidence: `evidence/s-core/F06/events.jsonl`.

| Order | Event | Time (ms) | Observed fact |
|---:|---|---:|---|
| 1 | old call entered R1 user code | 235.944 | BlockStarted |
| 2 | cut observed | 248.078 | call not completed |
| 3 | release old call | after 248.078 | returns 42 |
| 4 | replacement drain completes | after 255.375 | old call was allowed to finish |

The monotonic order proves the cut happened **after** the call had already entered business and that the result was not lost. It does not instrument the exact `TryAcquire` instant; “entered business” is the observable start claim.

## F08 — start claim wins, handler scheduling held

Canonical evidence: `evidence/s-core/F08/events.jsonl` (test commit `33fb48c2`, attempt `attempt-final-33fb`).

| Order | Event | Time (ms) | Observed fact |
|---:|---|---:|---|
| 1 | server interceptor entered | 401.783 | call has passed admission and is held before the generated handler |
| 2 | cut committed | 414.808 | call not completed; R1 service already activated (`oldR1Created=1`) |
| 3 | interceptor released | after 414.808 | handler runs |
| 4 | call completed | 429.212 | returns 2 from R1 |

This demonstrates that a call that already won the start claim/completed activation is not cancelled by the cut; it completes after the handler gate is released. It does **not** prove the scheduling pause is before all user code: the dynamic singleton constructor already ran (`oldR1Created=1`). The cut still cannot convert it into a new admission.

## F11 — three rapid sequential replacements

Canonical evidence: `evidence/s-core/F11/events.jsonl` (test commit `7d21b89a`, attempt `attempt-final-7d21`).

| Cycle | Event | Time (ms) | Old session → new session | R2 call |
|---:|---|---:|---|---:|
| 1 | replacement + refresh | 473.629 | `bb5b…` → `a474…` | 12 |
| 2 | replacement + refresh | 562.864 | `a474…` → `4ec4…` | 13 |
| 3 | replacement + refresh | 596.359 | `4ec4…` → `75ef…` | 14 |

Each cycle awaited the previous replacement and a new Ready session before the next call. This is **not** a late-Ack/out-of-order test: no old notification is delayed or replayed. F11's late-ack subcase remains `NOT_RUN`.

## F20 — retirement notice withheld (simulated loss)

Canonical evidence: `evidence/s-core/F20/events.jsonl` (test commit `29cf85de`, attempt `attempt-final-29cf`).

| Order | Event | Time (ms) | Observed fact |
|---:|---|---:|---|
| 1 | cut committed, notice barrier held | 374.370 | old session still Ready |
| 2 | 300 ms observation window | ~705.822 | still only the old session; no accidental reconnect |
| 3 | fresh call on old session | ~705.822 | `Unavailable`; R2 Created=0 |
| 4 | notice released (test continuation) | after 705.822 | new session appears; fresh call to R2 succeeds |

This is a **simulated** loss: the real frame is not sent while the barrier is held. It shows that no self-recovery occurs without the notice. Actual frame loss/duplication/reordering is `NOT_RUN`.

## F13 — not run

No Freeze/Resume implementation exists. There is no timeline to present. Marking this `NOT_RUN` is mandatory; it must not be inferred from F05/F07.

## F18 — idle ALC

Canonical evidence: `evidence/s-core/F18/events.jsonl`.

| Event | Time (ms) | Observed fact |
|---|---:|---|
| old session replaced | 353.347 | `newSessionIds` differs; `retiredServerConnections=0` |
| ALC check | 353.347 | `originalAlive=false` after 30 full GC/finalizer cycles |

## F18b — held duplex ALC retention and release

Canonical evidence: `evidence/s-core/F18b/events.jsonl` (test commit `4f927b40`, attempt `attempt-canonical-4f92`).

| Order | Event | Time (ms) | Observed fact |
|---:|---|---:|---|
| 1 | real duplex first item received | before 253.678 | stream is active; first item 2 consumed |
| 2 | cut/replace started | before 253.678 | `replaceCompleted=false` |
| 3 | held check | 253.678 | old session still Ready; old ALC alive; replacement blocked |
| 4 | stream gate released | 260.824 | second item 4 received; stream completes |
| 5 | replacement completes | after 260.824 | test awaited successful replacement/drain |
| 6 | ALC check | 364.207 | old ALC collected; new session Ready; retired connections 0 |

This is evidence that the pinned snapshot does **not** release the old ALC while a held duplex call still owns it, and does release it after the stream exits and the session retires. It does not cover Unregister, server-stream `MoveNext`/`Dispose`, or forced cancellation.

## F01–F04, F08, F11–F17, F19–F24

No event timelines exist. Do not construct them from source reasoning; the matrix marks them `NOT_RUN` or `NOT_IMPLEMENTED`.

