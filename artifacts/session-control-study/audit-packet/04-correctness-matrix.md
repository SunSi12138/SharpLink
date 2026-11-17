# 04 — Correctness Matrix (F01–F24)

Legend: `PASS` = the stated oracle was executed and met; `FAIL` = executed and violated; `INCONCLUSIVE` = some evidence but not enough for the oracle; `NOT_IMPLEMENTED` = the arm does not implement the mechanism; `NOT_RUN` = no execution.

Important: the S test harness pre-registers dynamic services **before** the physical client connect so that a session can pin a non-empty snapshot. That is a legitimate S precondition, but it is not the current P user flow. The unchanged P tests that register after connect fail on S (section 4.1).

## 4.1 Executed arm summary

| Arm | Implemented scope | Executed evidence | Gate |
|---|---|---|---|
| P (dev) | full current product | 528 integration / 2255 unit / 295 generator / 77 load; C-NEG | baseline only; F05 hazard confirmed |
| C-NEG | test-only negative control on P | F05 hazard | intentionally invalid; correctly detected |
| S | session-pinned snapshot + whole-session refresh on Replace/Unregister | F03/F05/F06/F07/F09/F10 + F18/F18b; full suite INCOMPLETE/HANG | **narrow TCP/Replace core gate PASS**; full F01–F24 gate NOT passed; existing tests fail |
| N | none | none | NOT_IMPLEMENTED |
| Q | none | none | NOT_IMPLEMENTED |
| H | none | none | NOT_IMPLEMENTED |

## 4.2 Matrix

| Case | P | N | C-NEG | S | Q | H |
|---|---|---|---|---|---|---|
| F01 local proxy before connect | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F02 control success vs delayed data manifest | `PASS` on P via `IndependentControlReply...`; does not prove N/S | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN; the unchanged P test fails on S with `Unimplemented` (recorded as a regression, not an S F02 result) | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F03 same ContractId/hash R1→R2 | NOT_RUN (P same-session behavior is the F05 hazard) | NOT_IMPLEMENTED | NOT_RUN | `PASS` — `Study_F03...`, new session, R1=1/R2=1, contract hash `a3054d…` unchanged | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F04 changed hash / old client | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F05 cut then held notification | `FAIL` — C-NEG hazard: stale request returns R2 value 2, R2 Created=1 | NOT_IMPLEMENTED | **PASS (hazard detected)** — `C_NEG_...`, R1=0/R2=1 | `PASS` — `Study_F05...`, stale `Unavailable`, R1=0/R2=0, cut hook held | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F06 admitted old call completes after cut | NOT_RUN (existing P behavior not isolated) | NOT_IMPLEMENTED | NOT_RUN | `PASS` — `Study_F06...`, blocked call returns 42 after cut | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F07 queued request vs cut | `PASS` — all 14 `QueuedSelectionRetirement...` rows | NOT_IMPLEMENTED | NOT_RUN | `PASS` — `Study_F07...`, queued `Unavailable`, R1=0/R2=0 | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F08 start claim wins cut | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN (F06 covers a blocked admitted call; F08 scheduling control not implemented) | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F09 unrelated B on same session | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | `PASS with cost` — `Study_F09...`, B still answers 7 but both A and B move to a new session; `foreignServiceInterrupted=true` | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F10 two sessions / delayed notifications | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | `PASS` — `Study_F10...`, both old sessions replaced, 2 notifications, both R2 calls correct | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F11 rapid R1→R2→R3 / late ack | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F12 ABA / slot reuse | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN (S has no request token; synthetic refresh generation only) | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F13 queued writer / Freeze | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F14 FreezeAck then held Resume | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F15 client-stream / duplex producer | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F16 server-stream MoveNext/Dispose tail | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F17 OneWay input | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F18 collectible ALC | `PASS` only for idle queued-selection fixture; held-stream not tested | NOT_IMPLEMENTED | NOT_RUN | **PASS for Replace** — `Study_F18...` idle replacement collects; `Study_F18b...` holds a real duplex enumerator across the cut, keeps the old session/ALC alive, releases it, then collects (`oldAlive=false`, retired connections 0). Unregister subcase NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F19 malformed/unauthorized identity | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F20 control loss/duplicate/reorder | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN — source review shows a single send with no retry; no fault-injection run | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F21 bounded tables / Stop | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F22 static Unary/OneWay TCP/SHM | NOT_RUN (full suite TCP passed; SHM environment-failed) | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F23 old wire/API identity mixed | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN (no wire change in S) | NOT_IMPLEMENTED | NOT_IMPLEMENTED |
| F24 repeated replace/reconnect then Stop | NOT_RUN | NOT_IMPLEMENTED | NOT_RUN | NOT_RUN | NOT_IMPLEMENTED | NOT_IMPLEMENTED |

## 4.3 S core gate decision

#998 requires F03/F05/F06/F07/F09/F10/F18 before performance. At the final test commit `4f927b40`, all seven executed cases pass for the **TCP/Replace** slice (F18 via idle + held-duplex). However, the required full F01–F24 matrix is not implemented:

```text
S core gate (TCP/Replace):   PASS
S full F01–F24 gate:         FAIL / INCOMPLETE
S performance:               NOT_RUN (full matrix gate not met)
```

F18's Unregister subcase, F08 and the streaming/physical-tail cases remain unrun. No performance comparison is valid for S.

## 4.4 C-NEG expected-failure check

The C-NEG test is deliberately designed to **fail against a nonexistent guarantee**. It passed because it observed the hazard, not because the P product is correct:

- event `publish-broadcast-held-after-commit` at `monotonicMs=496.252`;
- stale request returned `2` at `monotonicMs=529.102`;
- `oldR1Created=0`, `newR2Created=1`.

This is the required negative control. Evidence: `evidence/c-neg/`.

## 4.5 Evidence pointers

- S canonical events: `evidence/s-core/F03/events.jsonl` through `F18/events.jsonl`, plus `evidence/s-core/F18b/events.jsonl`; runner stdout in the same directories.
- S failed/mixed attempts: `evidence/s-core/failures/` (stdout logs), `evidence/s-core/F03-failed-attempt-2/`, `evidence/s-core/F18b-mixed-attempt/`.
- S full-suite partial log: `evidence/full-suite/stdout.log` and `evidence/full-suite/failure-names.txt`.
- S regression logs: `evidence/regressions/`.
- P focused tests: `evidence/p-baseline/focused/`.
- C-NEG: `evidence/c-neg/`.
