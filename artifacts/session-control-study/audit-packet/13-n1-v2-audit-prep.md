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

## 13.3A Desk-review observations (not independent findings)

These are source observations for the auditor, not product verdicts.

1. **Passive frame gate is not zero-cost.** `TransportFrameGate.Observe` (test fixture) copies every observed payload with `payload.ToArray()` and throws once `_frames.Count >= 512`. V2 adds `gate ??= new TransportFrameGate((ProtocolV2FrameType)255, false)` for cases that previously had no gate. The passive gate therefore records every frame and can hit the 512-frame cap, changing fixture memory/liveness and possibly turning a real RPC into a fixture failure. It is test-only, but it can distort the eight-case run; the first-failure snapshot must not be attributed to product code without separating this. **Verified without RPC:** a pure fixture probe invoked `Observe` with default header/empty payload and observed `FIXTURE frame inventory exceeded 512` on call 513; see `evidence/n1-review/passive-gate-probe.log`.
2. **Stop filter is intentionally broad.** `IsExpectedContractRefreshStopExit` is `stopStarted && exception is SharpLinkException { Code: ConnectionClosed }` (`SharpLinkClient.ContractSites.cs:311`). It does not prove the `ConnectionClosed` was caused by Stop; any ConnectionClosed after Stop starts is filtered. A pure probe confirms `stop+ConnectionClosed=True`, `stop+DeadlineExceeded=False`, `no-stop+ConnectionClosed=False`, `stop+InvalidOperation=False`; see `evidence/n1-review/stop-filter-probe.log`. The component control covers Deadline/unexpected faults, but not a genuine ConnectionClosed unrelated to Stop.
3. **Stop lock order.** `StopStaticClusterCoreAsync` takes `_poolGate`, then calls `StopContractSitesLocked()` (site gate) and `_frameworkTasks.Seal()`. The diagnostic path captures sites while holding the site gate but releases it before capturing cluster/pool state, so no obvious site→pool inversion was found; this still needs an independent lock-order audit, especially because the stop path holds `_poolGate` across site stop and framework-task seal.
4. **Diagnostic Lock reentrancy.** A local .NET 10 test confirms `System.Threading.Lock.TryEnter()` is reentrant (`nested TryEnter=True`). This removes one suspected self-deadlock in `NonBlockingDiagnostic.Capture(_gate, CaptureTrace)` followed by `lock (_gate)` inside `CaptureTrace`; it does not prove the rest of the diagnostic graph.
5. **No budget/assertion change observed.** The V2 `RpcProtocol.cs` diff changes diagnostics and observation plumbing; the 2 s request timeout, 5 s Prepare, 10 s Bind, 13 s timeout Prepare, 3 s guards and 50 ms reconnect were not changed. The passive gate and trace are the main behavioral deltas in the fixture.
6. **V1 remains unexplained.** None of the V2 changes target the first `PrepareAsync(5s)` deadline directly; a second hosted run would still be a diagnostic/discriminating run, not a repaired candidate.

The independent auditor should decide whether (1) and (2) invalidate any V2 run or require a fixture/source correction before authorization.

## 13.4 Pre-audit questions

1. **Nonblocking guarantee.** Inspect `NonBlockingDiagnostic<T>` and every `TryEnter` site. Is a contended gate guaranteed to return without waiting, and does a failed callback release every gate it acquired?

2. **Close ordering.** Does `ClientBindingSession.Close` signal the original Ack waiter before diagnostics, and can a diagnostic throw or contention delay physical cleanup? The components claim yes; verify source, not just the test.
3. **Stop filter.** Does `IsExpectedContractRefreshStopExit` apply only to a `ConnectionClosed` caused by an explicit Stop, and not swallow DeadlineExceeded, unexpected RPC failures or physical cleanup errors?
4. **Cluster Stop.** Does the V2 cluster path close Bind work admission and return/seal sites without running callbacks while holding `_poolGate`? Are old keys unable to require GC?
5. **Passive gate.** Is the `(ProtocolV2FrameType)255` gate passive for every case, and does it preserve exact staged/inbound byte order and actual inner flush completion? The fixture probe confirms it throws at the 513th observed frame; the auditor must decide whether any of the eight cases can reach 512 frames and whether the passive gate must be made header-only before a hosted run.
6. **Trace overhead.** Is trace default-off in production, and does `EnableContractBindingTraceForDiagnostics` avoid adding per-RPC work when disabled? The N1 evidence enables it for all clients; a production hot-path claim must not be inferred from that.
7. **First-failure diagnosis.** V2 adds a failure snapshot, but it is a recovery-time sample, not an atomic snapshot of the first Prepare. Can it actually distinguish “no eligible Session”, “predecessor blocked”, “send/flush blocked”, “Ack received but unpublished”, “confirmation failed”, and “confirmed but not published”?
8. **V1 failure remains.** Does any V2 change plausibly fix the first `PrepareAsync(5s)` DeadlineExceeded? If not, a second hosted run would still be a diagnostic run, not a repaired candidate.
9. **Authorization.** The N1 protocol says V1 consumed the only hosted authorization and V2 needs independent review/root decision. A second run must not be launched as an automatic retry.
10. **Scope.** N1 is static Unary/no-input OneWay only; it is not N0 and cannot satisfy the #998 N arm or performance gate.

## 13.5 Recommendation

- Do **not** run N1 RPC locally.
- Complete an independent V2 source/protocol audit using this checklist.
- Resolve the passive-gate 512-frame/payload-copy risk before any hosted run; either bound it explicitly for the eight cases or make the passive path header-only without changing the gated cases.
- If the independent auditor finds the diagnostic/Stop changes sound, obtain explicit root authorization for exactly one hosted run with SDK 10.0.115/runtime 10.0.15 and the frozen 8-case protocol.
- Preserve V1 failure and do not overwrite it.
- Only after N1 passes all 8 cases should the N0 group/receipt/dynamic/five-family work begin.

Current status: **N1 NOT_QUALIFIED; no second run authorized; N0 NOT_IMPLEMENTED; S NO-GO; overall INCONCLUSIVE.**
