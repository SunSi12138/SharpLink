# 14 — N1 V2 Independent-Context Audit Report

Status: **INDEPENDENT-CONTEXT AI AUDIT — NOT AN EXTERNAL HUMAN AUDIT**. This report was produced by a fresh-context subagent that was given only the N1 source, the V2 diff, the frozen protocol and the probe evidence. It is independent of the author’s narration, but it is the same model family, not a third-party human reviewer. The packet’s external audit request remains `PENDING_INDEPENDENT_AUDIT`.

## 14.1 Verdict

```text
FIX_BEFORE_HOSTED
```

The diagnostic direction is sound, but V2 cannot yet be used as a hosted diagnostic subject:

- the passive transport decorator can throw into the product data path;
- the new Stop filter suppresses failures by global flag rather than causality;
- nothing in V2 fixes the V1 first-Prepare failure, so a second run would be diagnostic only, never a functional qualification.

## 14.2 Proven facts

- HEAD `e901bcfc` is V2 `131df413` plus one line in `global.json` (10.0.102→10.0.404). The hosted script requires SDK 10.0.115; the hosted subject must be `131df413`, not HEAD.
- `NonBlockingDiagnostic<T>` uses zero-wait `TryEnter` for `Lock`/`Monitor`, catches capture exceptions and exits in `finally` (`src/SharpLink.Runtime/NonBlockingDiagnostic.cs:7-23`). Reentrancy is required and is safe for the reviewed call sites.
- `ClientBindingSession.Close` signals all original Ack waiters under `_gate` before diagnostics, releases the gate, then performs best-effort capture (`src/SharpLink.Client/ClientBindingSession.cs:239-250`). Capture can delay but not block cleanup; it runs synchronously inside `ClientConnection.Fail` before cancellation/pending-call failure and `DisposeAsync`.
- `IsExpectedContractRefreshStopExit` is exactly `stopStarted && SharpLinkException { Code: ConnectionClosed }` (`src/SharpLink.Client/SharpLinkClient.ContractSites.cs:224-225`, called at `:311`). `_stopStarted` is set at `StopAsync` entry before shutdown work (`src/SharpLink.Client/SharpLinkClient.cs:142-158`).
- Stop lock order is `_poolGate` → `_contractSiteGate` → site `_gate`, and `_poolGate` → framework `_gate` (`SharpLinkClient.cs:173-179`, `:231-236`; `ContractSites.cs:226-233`, `:282-285`; `FrameworkTaskSupervisor.cs:158-170`). The reverse pool acquisition is `TryStartBindingWork` (`ContractSites.cs:54-62`). No inverse site/framework → pool path was found in the reviewed stop/site code.
- `site.Stop()` completes `_changed` with `RunContinuationsAsynchronously` (`ContractSites.cs:261`); `Seal` completes `_drainSignal` outside its gate, under `_poolGate`, with `RunContinuationsAsynchronously` (`FrameworkTaskSupervisor.cs:184-190`). No inline user callback under the new Stop locks was proven.
- **Passive gate (verified):** `TransportFrameGate.Observe` copies the full payload (`payload.ToArray()`) and throws `InvalidOperationException("FIXTURE frame inventory exceeded 512.")` at frame 513 (`test/SharpLink.N1.StaticBindEvidence/TransportFrameGate.cs:46-55`). It is called from the product write path (`:78-80`) and read path (`:145-148`). V2 wraps the server listener and every client even when no gate is supplied (`RpcProtocol.cs:379-386`, `:403-405`).
- Trace is disabled by default (`ContractBindingTrace.cs:36-38`); the evidence harness enables it for every client (`RpcProtocol.cs:405`, `:420`).
- Eight cases, order, budgets, guards, reconnect/admission settings are unchanged (`RpcProtocol.cs:20`, `:54-68`, `:347-361`). Some observation checks changed from blocking diagnostics to trace/atomic counters; case semantics and timeouts are the same.
- V1’s first failure is not addressed: the first oracle is still `PrepareAsync(TimeSpan.FromSeconds(5))` at `RpcProtocol.cs:93`; Prepare/refresh/Bind/Ack protocol code is unchanged. V2 only adds phase locks/counters in that path. The author’s failure audit itself says no unique cause and no repair (`doc/validation/issue-775-n1-prepare-failure-audit.md:23-30`).
- N1 remains a non-release static checkpoint, not N0 (`doc/issue-775-n1-static-bind-checkpoint.md:3`, `:16`, `:41-43`).

## 14.3 Blocking issue — fixture run integrity

`TransportFrameGate.Observe` throws **inside the actual transport path** at the 512-frame bound, while the protocol describes it as a passive observer and says the frame inventory is bounded at 512. A diagnostic limit must never fault the product.

The eight-case traffic is estimated to be well below 512 frames (RawGuards on the order of ~150; other cases tens), so the cap is unlikely to be reached in the current script. That does not make a throw into `FlushAsync`/`AdvanceTo` acceptable. The full-payload copy also makes the gate non-passive in timing/memory terms and retains up to 512 payload arrays per gate.

**Required fix:** drop observations after the cap (or reset per case), count drops, never throw into `FlushAsync`/`AdvanceTo`; store only header/size/control token, not full payloads. If full payload retention is kept, cap retained bytes and treat the run as timing-perturbed.

## 14.4 P1 findings

1. **Stop filter is causally overbroad.** Any `SharpLinkException(ConnectionClosed)` after `_stopStarted` is returned as expected, regardless of whether Stop caused it (`ContractSites.cs:224-225`, `:311`). It can swallow a peer/transport-initiated close, a protocol close mapped to `ConnectionClosed`, or a bind failure during shutdown. The most exposed case is `site-waiter-hard-bounds` (`RpcProtocol.cs:293`), the only in-body Stop with 64 live refresh/prepare sites; every cleanup Stop is a secondary masking path. The controls are tautological: `DiagnosticComponents.cs:89-94` only assert true+ConnectionClosed and false+ConnectionClosed; they never supply a non-Stop ConnectionClosed with `stopStarted=true`.
2. **V2 does not fix the V1 failure.** A second run can only be diagnostic. The only product changes in the binding path are extra phase locks/counters; the only transport change is extra gating/copying; both can add latency. A V2 PASS would not prove the first Prepare failure was fixed.
3. **Always-on gating/tracing changes the subject.** Every client gets a gate and trace (`RpcProtocol.cs:403-405`), and every default case now uses the passive recorder. This is a new timing/GC/pipe path for the exact first Prepare that V1 failed. Any performance/timing conclusion from the run is invalid, and reproduction of V1 is unverified.
4. **The V1 Stop guard is not proven fixed.** The new site-stop path closes sites and blocks new bind work, but it does not cancel an in-flight `RefreshCoreAsync` awaiting `BindAsync`. The 6 pure controls are unconnected/no-RPC controls (`DiagnosticComponents.cs:95-115`); they do not exercise the V1 live-session Stop scenario.

## 14.5 P2 findings

- Diagnostic memory is not byte-bounded. `_frames` bounds count at 512 but stores full payloads; `RecordFlush` stores a live `Task?` per session (`TransportFrameGate.cs:25-31`, `:50-51`). The serialized snapshot keeps only metadata (`FailureDiagnostics.cs:56-68`), but the fixture objects retain payload/live flush references.
- Failure output has no aggregate byte cap. `FailureDiagnostics.Write` serializes the whole snapshot to stderr; `Program` also writes `firstFailure.ToString()`, which can contain exception messages/stack (`FailureDiagnostics.cs:67-68`, `Program.cs:107-118`). Bounds are per-collection only.
- `Committed` is written under `_contractSiteGate` and read under site `_gate` in `CaptureTrace` (`ContractSites.cs:166`, `:456`); the trace can report a stale value. Diagnostic-only.
- V2 adds unconditional phase/counter writes in bind/refresh even when trace is disabled (`ContractSites.cs:319`, `:325`, `:336`, `:385`). The trace enable itself is gated; this per-bind lock work is not.
- `ReceivedUnpublishedAcksNonBlocking` requires every ready-session snapshot to be available; one contended session returns null and can turn a real Ack observation into a 3 s guard/INCONCLUSIVE (`RpcProtocol.cs:31-36`).
- `IsExpectedContractRefreshStopExit` handles only `ConnectionClosed`, not `Unavailable`/OCE, while the generic supervisor classifier handles more (`SharpLinkClient.cs:275-283`). The early-Stop window can still record a framework fault for another stop-induced exception.
- `HoldContractSiteIndexForDiagnosticTest` is a blocking test hook in product source; it holds the real site-index gate while `body()` runs (`ContractBindingTrace.cs:54-59`). Test-only, but lifetime-owner-shaped.
- HEAD `global.json` 10.0.404 conflicts with the hosted script’s required 10.0.115. The hosted subject must be `131df413`; the review branch is component-only.

## 14.6 Unverified

- Exact frame count per gate/case; whether any eight case reaches 512. The estimate is well below, but not measured.
- Root cause of V1’s first Prepare `DeadlineExceeded`; whether V2’s snapshot captures the failing phase; whether V2 changes timing enough to prevent reproduction.
- Whether V2 fixes the V1 3 s client-Stop guard; the pure controls do not exercise live RPC/Session Stop.
- Serialization of every failure-snapshot shape (polymorphic `object`, large frames/logs) and whether it can block on stderr.
- Whether the Stop filter masks a real `ConnectionClosed` in a run; case 7 is the concrete risk, runtime required.
- Lock-order inversions outside the reviewed client/site/supervisor paths.

## 14.7 Recommendation

1. **Do not authorize a second hosted run on current V2/HEAD.** Fix first:
   - make the passive gate drop/count after its cap, never throw into the transport path, and stop storing full payloads;
   - scope the Stop filter to actual Stop causality or remove it from the diagnostic branch; add a control where a non-Stop `ConnectionClosed` occurs after `_stopStarted` and assert it still faults;
   - keep failure capture nonblocking, cap serialized output, avoid blocking client diagnostics in the failure path;
   - decide whether always-gating/tracing every client is acceptable; for V1 reproduction use a header-only recorder that does not gate default transports, and label timing perturbation;
   - if claiming the Stop fix, add a live-session/cluster Stop control, not only unconnected site/waiter controls.
2. After fixes, root may authorize at most one hosted run explicitly labeled **diagnostic only**, against source `131df413`, UID `N1-STATIC-BIND-20261115-V2`, no retry, preserving first-failure snapshots. It cannot qualify N1; a PASS only means the diagnostic protocol ran.
3. If root does not authorize after fixes, keep V2 unreleased. V1 authorization is consumed; do not treat V2 as a continuation/retry of V1.
4. Use V2 `131df413`, not HEAD `e901bcfc`, for any hosted subject; HEAD’s `global.json` cannot satisfy the hosted SDK check.

## 14.8 Scope

N1 proves only a static Unary / no-input OneWay wire-6 binding checkpoint: one canonical static site, exact token dispatch, bounded Bind/queue/flush/Ack, fixed/cluster selection. It does **not** prove input OneWay, client/server streaming, duplex, dynamic local contract/registration/ABA, group/receipt publication, all callers/tests/samples/docs/packages, Trim/AOT/#805/canonical, cost/performance, Windows/SHM, or product readiness.

Correct status: **functional qualification NOT passed; V1 produced zero completed RPC cases; V2 is an unqualified diagnostic revision; cost qualification HOLD; no product PR/merge.**
