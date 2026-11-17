# 09 — Final Comparison and Decision

Status: **PENDING_INDEPENDENT_AUDIT**. This is a research comparison, not an adoption decision and not a merge approval.

## 9.1 Bottom line

| Question | Answer |
|---|---|
| Does P exhibit the stale-request retarget hazard? | **Yes, proven.** C-NEG on P: stale request after Replace commit, before manifest notification, executes R2 (value 2; R1 Created=0, R2 Created=1). |
| Does the narrow S prototype stop that hazard? | **Yes for the exercised TCP/Replace window.** F05: stale request returns `Unavailable`, R1=0, R2=0; replacement session then works. |
| Is S a viable replacement for N0? | **NO-GO in current form.** It breaks dynamic registration after connect, retires unrelated services with the session, and has no recovery for a lost refresh frame. Its narrow core gate passes, including a held-duplex F18b, but the full F01–F24 matrix is not complete. |
| Is there a valid N-vs-S performance comparison? | **No.** N is not implemented; S is not correctness-qualified; no A/A or paired samples were run. `INCONCLUSIVE`. |
| Should #775's selected N0 architecture be changed now? | **No evidence supports changing it.** The experiment found a narrow safety mechanism but not a viable same-session replacement. |

Overall experiment result: **INCONCLUSIVE**, with an explicit **NO-GO for S as an N0 substitute**. The N0 design remains the reference; this is not an approval to implement or merge S.

## 9.2 Arm comparison

| Dimension | P (dev) | N (N0) | S (prototype) | Q | H |
|---|---|---|---|---|---|
| Request identity | ContractId + MethodId | BindingId + MethodId (designed) | ContractId + MethodId, resolved only in the session-pinned snapshot | ContractId + MethodId with global barrier (designed) | BindingId + MethodId (designed) |
| Cut / admission | live registry lookup; publish before drain cut | whole-group cut before publication + exact token | module `TryBeginDraining` before new snapshot publication; per-session snapshot blocks R2 | freeze + drain + ack + resume; not implemented | N0 exact-entry admission |
| Old request after cut | can hit R2 (proven) | exact old entry rejects, no retarget | rejected with `Unavailable`; no R2 (F05) | unproven | exact rejection |
| Unrelated service B | unaffected by A's registry swap (same session remains) | local binding invalidation; session unchanged | **whole session replaced** (F09), B moves | would depend on barrier scope | session unchanged |
| Dynamic registration after connect | works (P tests pass) | Bind on existing data session | **broken**: session snapshot lacks new contract; existing tests fail | unproven | works via Bind |
| Long stream / ALC | existing P behavior; not isolated | physical owner retained until exit | idle and held-duplex F18 pass; Unregister/server-stream tails not run | not implemented | designed |
| Control frame | manifest broadcast only | Bind/Invalidated/Release/RequestAccepted | one `SessionRefreshRequested`; no ack/resend | freeze/resume frames not implemented | N0 controls |
| Hot-path change | 1 live registry lookup | exact slot/token lookup (designed) | 1 pinned-dictionary lookup; no per-RPC allocation added | would add gate/queue checks | exact N0 path |
| Implementation size on branch | — | **0** | 133 product insertions / 10 files; 1,944 test/model insertions | 0 | 0 |
| Correctness evidence | full P baseline + C-NEG | N1 static checkpoint exists; V1 hosted FAIL, V2 63 pure checks, no RPC | 13 cases PASS/PARTIAL (core, F04, F08, F11, F20, F24), including F18 idle and F18b held duplex; full F01–F24 not run; existing tests FAIL | none | none |
| Performance evidence | none | none | none | none | none |
| Verdict | baseline / unsafe under F05 | reference design, not implemented | **NO-GO research prototype** | **NOT_IMPLEMENTED / NO-GO** | **NOT_IMPLEMENTED** |

## 9.3 Proven facts

1. `dev` has no N0 binding path; 16-byte prefix is still `ContractId + MethodId`.
2. P's `ReplaceAssemblyAsync` originally published the new `_services` before `TryBeginDraining`; C-NEG proves the resulting stale-request retarget on a real TCP server/client.
3. The S prototype, after `525a5aed`, moves `TryBeginDraining` before the new snapshot publication and routes requests from a per-session frozen table. The narrow F05 window is closed in the tested harness.
4. S's F06 blocked R1 call completes with 42 after the cut.
5. S's F07 queued request returns `Unavailable` after losing the cut; neither R1 nor R2 user code runs; queue/permit counts return to zero.
6. S's F09 unrelated B remains functionally correct but is moved to a new physical session.
7. S's F10 moves two independent sessions to new snapshots and routes both fresh calls to R2.
8. S's F18 idle replacement and F18b held-duplex release both collect the old service ALC while client/server remain alive; F18b additionally proves the old session/ALC is retained and the replacement is blocked while the stream is active.
9. S breaks existing dynamic-registration-after-connect behavior: `IndependentControlReply...` fails with `Unimplemented`; `QueuedSelection...` times out. The full S suite additionally **hangs** after 63 unique observed failures; it is `INCOMPLETE/HANG`, not a pass rate.
10. P's baseline test runs are environment-limited: 46 Integration, 26 Unit, 1 Generator and 4 Load failures, dominated by SharedMemory/TLS/UDS/temp ACL denials.
11. At test commit `4f927b40`, the seven required core cases and the extra F18b held-duplex case all pass on TCP/Replace; at `33fb48c2`, F08 also passes; at `948fb44d`, F04 fails closed without entering R2. This is a **narrow core/F08/F04 pass**, not a full F01–F24 pass.
12. F08 uses a real server interceptor to hold the handler after admission; the cut occurs while the call is pending and release returns the original R1 result 2. The singleton constructor had already run, so this is not a pure “no user code before cut” proof.
13. F04 is fail-closed: after a changed-hash contract replacement, the old client gets `Unavailable` and R2 is not entered. However, no replacement session becomes Ready within 2 s, so the old client cannot recover without a new application deployment. This is an availability cost, not a retargeting failure.
14. F11 performs three sequential replacements, each creating a new physical session and routing a fresh call to the new registration. It does **not** exercise late or out-of-order old notifications.
15. F20 withholds the retirement notice: the old session remains, a fresh call returns `Unavailable`, R2 is not entered, and releasing the notice recovers. This simulates loss, not a real dropped frame; duplicate/reorder are not tested.
16. F24 performs five replacements/reconnects and then client Stop; public counters quiesce to `serverActiveConnections=0` and `clientReadySessions=0`. This is not a full framework-task/snapshot/ALC census.
17. At `9ad6fe74`, all 13 study tests pass in one process (`13/13`, 0 failed, 0 skipped), which is evidence of test isolation/order stability but not a substitute for the individual attempts.
18. The separate N1 static-bind checkpoint (`131df413`) builds and its 63 pure-component checks pass locally, but the only hosted RPC run failed at the first Prepare and V2 has no second-run authorization. N1 remains `NOT_QUALIFIED`; it is not the N0 arm and no N RPC case was run.
19. Pre-audit source review found two V2 risks that must be resolved before a second hosted run: the passive test gate copies every frame payload and a pure fixture probe verified it throws `FIXTURE frame inventory exceeded 512` on the 513th observation; the Stop filter suppresses any `ConnectionClosed` after Stop starts, not only one caused by Stop. These are test/source risks, not proven N1 product failures.

## 9.4 Source-based analysis (not measured)

- S's cut is linearizable at the `TryBeginDraining` CAS for dynamic module admission. A request that wins `TryAcquire` before the CAS is an admitted old call; one that observes `Draining` is rejected.
- The per-session snapshot prevents an old session from ever resolving R2, which is why the C-NEG hazard is closed without a per-request token.
- The cost is paid in session lifetime: the snapshot strongly holds all `ServiceRegistration` objects, and a long-lived session/stream can keep the old module and ALC alive.
- S's control frame is a single send with no acknowledgement or resend. A lost frame leaves the client on a draining old session; new calls fail `Unavailable`. This is not unsafe retargeting, but it is a permanent availability failure until an external reconnect.
- `RegisterAssembly` has no S retirement notification. The current API expects dynamic registration to become usable on a live session; S requires a new handshake. This is a functional regression, not just a performance tradeoff.
- `SessionRefreshRequested` was designed for desired-session configuration and blue/green refresh. Reusing it for registry retirement changes its meaning without a new capability/ack contract.

## 9.5 Hypotheses not proven

- That the held-duplex release result generalizes to server-stream `MoveNext`/`Dispose`, forced cancellation, and Unregister.
- That a control frame loss can be recovered by the existing client reconnect policy without an application-visible outage.
- That S could add a Register notification and stay within bounded session churn.
- That S would reduce hot-path cost; no measurement exists.
- That Q could provide a global barrier without deadlock or unbounded queue/drain state.
- That N can be implemented within the N0 cost envelope; no current N candidate exists.

## 9.6 Protocol-complexity judgment

S is small in source (133 product lines) but not simple in semantics:

- it changes the lifetime unit from a registration to an entire physical session;
- it makes the session snapshot a new authority that must be reconciled with Register, Replace, Unregister, reconnect and Stop;
- it has no exact token/receipt, so it cannot replace N0's exact-identity obligations;
- it reuses a control frame whose existing state machine was built for a different trigger;
- it introduces a new failure mode (lost refresh) that P did not have.

The apparent reduction in code is therefore a **transfer of complexity** to session lifecycle and availability, not an elimination of the binding problem.

## 9.7 Resource-safety judgment

`INCONCLUSIVE`. Idle and held-duplex old-module/ALC release are real: F18b shows the old session and ALC retained while the duplex stream is active, then collected after release and session retirement. Unregister, server-stream `MoveNext`/`Dispose`, producer/consumer, CTS and codec/facts physical exit are not measured. A long stream can keep the pinned old session and ALC alive by design; the duplex path shows it is released after completion, but the broader owner graph remains unproven.

## 9.8 Decision

```text
S as an N0 substitute:       NO-GO
N (N0):                      NOT_IMPLEMENTED
N1 static checkpoint:        NOT_QUALIFIED (V1 hosted FAIL; V2 components only)
Q as a candidate:            NO-GO (unimplemented; no happens-before proof)
H:                           NOT_IMPLEMENTED
P baseline:                  unsafe under F05; not a candidate
Overall architecture study:  INCONCLUSIVE
#775 decision:               retain N0; no evidence to change it
```

This is **not** a GO to merge N0 either; N0 remains a design/contract, not a working candidate. The only supported research conclusion is that the session-snapshot idea can close the narrow stale-request window, but its current form is not a viable replacement for exact per-request binding. No performance conclusion is possible.
