# 13 — N1 V2 Pre-Audit Preparation

Status: **PRE-AUDIT ONLY / NOT AUTHORIZED TO RUN**. This document prepares an independent review of the N1 V2 protocol and source. It is not an independent audit and does not authorize a second hosted run.

## 13.1 Source and diff

| Item | Value |
|---|---|
| V1 source | `be24fda408118e5a9155174b8ac3c6b794e46aa9` |
| V2 source | `131df413db8e222a335ad15561dd10612dece6a3` |
| Review branch | `study/998-n1-review` at `e901bcfc907831e7d9191afb8828507c20300f58` (only global.json 10.0.404) |
| V1→V2 diff | 20 files, +654/−39 |
| Protocol | `doc/validation/issue-775-n1-static-bind-protocol.md` |
| V1 failure audit | `doc/validation/issue-775-n1-prepare-failure-audit.md` |

The full V1→V2 diff can be reproduced with:

```text
git diff be24fda4..131df413
```

## 13.2 What V2 changes

V2 is **not documentation-only**. The diff touches:

- `src/SharpLink.Client/ClientBindingSession.cs`: failure/close diagnostic sampling and trace;
- `src/SharpLink.Client/ContractBindingTrace.cs` (new): bounded trace and nonblocking gates;
- `src/SharpLink.Client/SharpLinkClient.ContractSites.cs`: stop-aware expected refresh exit;
- `src/SharpLink.Client/SharpLinkClient.cs`: trace enablement/Stop changes;
- `src/SharpLink.Client/SharpLinkClient.{ClusterSupportTopology,DynamicCluster,EndpointCluster}.cs`: nonblocking capture;
- `src/SharpLink.Runtime/{FrameworkTaskSupervisor,NonBlockingDiagnostic}.cs`: supervisor counter and nonblocking diagnostic;
- `src/SharpLink.Server/SharpLinkServer.cs`: diagnostic capture hook;
- tests: `ContentionComponents`, `DiagnosticComponents`, `FailureDiagnostics`, `RpcProtocol`, `TransportFrameGate`.

The protocol's eight RPC cases and budgets were not changed in the inspected diff. `UseRequestTimeout(TimeSpan.FromSeconds(2))`, the 5 s Prepare, 10 s Bind, 13 s timeout Prepare, 3 s guards and 50 ms reconnect remain. The diff does add a passive `TransportFrameGate((ProtocolV2FrameType)255, false)` for cases that previously had no gate; this is a diagnostic decorator, but an auditor should verify it is byte-transparent and does not alter flush/pump behavior.

## 13.3 What the 63 components prove

On SDK 10.0.404 (not the N1 protocol's 10.0.115), six pure modes passed:

| Mode | Checks |
|---|---:|
| components | 23 |
| selection-components | 4 |
| contention-components | 10 |
| cluster-stop-components | 6 |
| diagnostic-components | 9 |
| publication-components | 11 |
| **Total** | **63** |

They cover wire shape, facts, quota hard limits, Picker preservation, nonblocking diagnostic gates, cluster Stop site return, publication isolation and generated API6/facts. They do **not** execute a single real TCP RPC, Bind/Ack/flush, server admission, Codec/activation, ALC or Stop owner.

## 13.4 Pre-audit questions

1. **Nonblocking guarantee.** Inspect `NonBlockingDiagnostic<T>` and every `TryEnter` site. Is a contended gate guaranteed to return without waiting, and does a failed callback release every gate it acquired?
2. **Close ordering.** Does `ClientBindingSession.Close` signal the original Ack waiter before diagnostics, and can a diagnostic throw or contention delay physical cleanup? The components claim yes; verify source, not just the test.
3. **Stop filter.** Does `IsExpectedContractRefreshStopExit` apply only to a `ConnectionClosed` caused by an explicit Stop, and not swallow DeadlineExceeded, unexpected RPC failures or physical cleanup errors?
4. **Cluster Stop.** Does the V2 cluster path close Bind work admission and return/seal sites without running callbacks while holding `_poolGate`? Are old keys unable to require GC?
5. **Passive gate.** Is the `(ProtocolV2FrameType)255` gate passive for every case, and does it preserve exact staged/inbound byte order and actual inner flush completion?
6. **Trace overhead.** Is trace default-off in production, and does `EnableContractBindingTraceForDiagnostics` avoid adding per-RPC work when disabled? The N1 evidence enables it for all clients; a production hot-path claim must not be inferred from that.
7. **First-failure diagnosis.** V2 adds a failure snapshot, but it is a recovery-time sample, not an atomic snapshot of the first Prepare. Can it actually distinguish “no eligible Session”, “predecessor blocked”, “send/flush blocked”, “Ack received but unpublished”, “confirmation failed”, and “confirmed but not published”?
8. **V1 failure remains.** Does any V2 change plausibly fix the first `PrepareAsync(5s)` DeadlineExceeded? If not, a second hosted run would still be a diagnostic run, not a repaired candidate.
9. **Authorization.** The N1 protocol says V1 consumed the only hosted authorization and V2 needs independent review/root decision. A second run must not be launched as an automatic retry.
10. **Scope.** N1 is static Unary/no-input OneWay only; it is not N0 and cannot satisfy the #998 N arm or performance gate.

## 13.5 Recommendation

- Do **not** run N1 RPC locally.
- Complete an independent V2 source/protocol audit using this checklist.
- If the independent auditor finds the diagnostic/Stop changes sound, obtain explicit root authorization for exactly one hosted run with SDK 10.0.115/runtime 10.0.15 and the frozen 8-case protocol.
- Preserve V1 failure and do not overwrite it.
- Only after N1 passes all 8 cases should the N0 group/receipt/dynamic/five-family work begin.

Current status: **N1 NOT_QUALIFIED; no second run authorized; N0 NOT_IMPLEMENTED; S NO-GO; overall INCONCLUSIVE.**
