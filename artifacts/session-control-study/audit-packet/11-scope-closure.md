# 11 — Scope Closure and Hard Blockers

Status: **PENDING_INDEPENDENT_AUDIT**. This file records why the study stops here and what is still missing. It is not a claim that #998's full matrix or performance stage is complete.

## 11.1 What is complete

- G0 baseline/source freeze at `dev @ 2948de82`.
- G1 executable model (`MODEL PASS`).
- G2 P real baseline tests (Integration, Unit, Generator, Load; counts and environment failures preserved).
- C-NEG negative control on P (stale request enters R2).
- S prototype with cut-before-publish and whole-session retirement.
- 13 study tests: F03, F04, F05, F06, F07, F08, F09, F10, F11, F18, F18b, F20, F24.
- 13/13 all-study batch at `9ad6fe74` (one process, 4.842 s).
- G4 architecture/resource review.
- G7 audit packet and independent audit request.

## 11.2 What is not complete

| Gap | Why it is not complete |
|---|---|
| F01/F02 | No S local-proxy/receipt implementation; these are N0/API obligations, not implemented |
| F11 late/out-of-order notification | sequential replacements only; no delayed/replayed control frame |
| F12 ABA/slot reuse | S has no request token; only a synthetic refresh generation |
| F13/F14 Q freeze/drain/resume | Q is not implemented |
| F15/F16/F17 | producer/consumer, server-stream MoveNext/Dispose, OneWay input not tested for S |
| F18 Unregister subcase | Replace path only |
| F19 | malicious/expired identity and per-request authorization not covered |
| F20 duplicate/reorder | simulated loss only |
| F21 bounded tables/capacity + Stop | no capacity injection |
| F22 performance-width Unary/OneWay TCP/SHM | not a correctness goal; no eligible performance candidate |
| F23 old wire/API mixed | S has no wire change; no mixed-peer run |
| N | N0 `NOT_IMPLEMENTED`; N1 static checkpoint `131df413` is `NOT_QUALIFIED` and its V2 audit verdict is `FIX_BEFORE_HOSTED` |
| Q | `NOT_IMPLEMENTED` — no freeze/send-gate/drain/ack state machine |
| H | `NOT_IMPLEMENTED` |
| G5/G6 performance | forbidden by #998 until the full correctness matrix passes; no A/A or ABBA/BAAB was run |

## 11.3 Hard blockers

1. **No qualified N0 candidate.** `dev` has no BindingId/BindContract/BindAck/Invalidated/Release/RequestAccepted, no wire 6, no receipt/ABA/queue-only cut. The separate N1 static checkpoint (`131df413`) implements a static-only wire-6 slice, but its only hosted run failed at the first Prepare and V2 has no second-run authorization; the V2 audit verdict is `FIX_BEFORE_HOSTED`. It is not N0 and cannot support a valid N-vs-S comparison.
2. **No Q candidate.** Q requires a client send gate, server cut, writer flush/drain, FreezeAck and ResumeAck. None exists. Implementing it is a separate protocol project, not a test-only addition.
3. **Performance gate.** #998 forbids performance for a candidate that has not passed the full F01–F24. S has 13 tested cases, not the full matrix. Running A/A or ABBA/BAAB now would violate the pre-registration and produce invalid evidence.
4. **Transport capability.** SharedMemory mapping/TLS PFX/UDS bind are denied on this Windows host; 46 Integration, 26 Unit, 1 Generator and 4 Load baseline failures are preserved. TCP is executable; the requested cross-transport matrix is not.
5. **Toolchain deviation.** The repository requests SDK `10.0.102`; the host has `10.0.404`. The study uses `10.0.404` with a declared `global.json` change. This is an environment deviation, not a silent substitution.
6. **No CI/Actions evidence.** All runs are local. There is no GitHub Actions run URL or artifact; the packet says so explicitly.

## 11.4 Why further narrow S tests do not change the decision

S's NO-GO is driven by observed behavior, not by a missing test:

- existing `IndependentControlReply...` fails with `Unimplemented` because a session pins an empty/old snapshot;
- existing `QueuedSelectionRetirement...` times out for the same reason;
- the full S Integration suite hangs after 63 unique failures;
- the control frame is single-shot and F20 shows no self-recovery when it is withheld;
- whole-session collateral is observed in F09;
- changed-hash old clients fail closed but cannot recover (F04).

Adding F15/F16/F19/F21 tests would improve coverage but cannot make S a same-session N0 replacement. The correct research decision is **S NO-GO** and **overall INCONCLUSIVE** until an N/Q candidate and a full matrix exist.

## 11.5 Minimal future work order

1. Independently review N1 V2 and authorize or reject a second hosted run of the exact 8-case protocol; do not substitute the 63 pure-component checks for RPC evidence.
2. Implement N0 exactly (wire 6, Bind/Invalidated/Release/RequestAccepted, exact token/receipt/queue cut, physical owners) on an isolated branch; pass the full F01–F24.
3. Add Q only if a complete client/server freeze-drain-ack state machine and a happens-before proof exist.
4. Re-run S's full F01–F24 and fix the dynamic-registration, notice-loss and held-owner gaps.
5. Only then run the pre-registered A/A and ABBA/BAAB matrix on a host with TCP and SharedMemory/TLS/UDS capabilities.
6. Independent audit of the packet.

Until then, no performance conclusion and no architecture change to #775 is justified.

## 11.6 Final status

```text
Experiment scope:       PARTIAL, honestly closed
S:                      NO-GO
N (N0):                 NOT_IMPLEMENTED
N1 static checkpoint:   NOT_QUALIFIED / FIX_BEFORE_HOSTED
Q/H:                    NOT_IMPLEMENTED
Performance:            NOT_RUN
Overall study:          INCONCLUSIVE
Independent audit:      PENDING_INDEPENDENT_AUDIT
```
