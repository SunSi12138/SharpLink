# BS1 — bootstrap/supervision, narrow real-TCP screen

## Sources and attribution

| Arm | Published source | Full tree | Local reviewed source |
|---|---|---|---|
| dev | `57f06f172d29fa5cfd4ea8e9b86a23f122df24ab` | `e5b72625377ffdcf5f5470e2ddd69be12a449d8e` | same |
| A | `ed34529fd660484c546f9ac96f73e322b5e04312` | `806337ad391f691b477a47d9349694fac95ed23f` | `d22776e73e050ef5a8076974943eac67f7725ac8` |
| B | `13e75f15f16cb98960c97f9500e17d26ba81f43b` | `025dba3a4756227e062eab005b2693fc24a62f6a` | `e9f81d9a543b2f3368b54ba5d5c3a5068b5d5fdc` |

Full-tree git fetch/readback equality and published ancestry have been verified. A removes unused completed-success task registrations; B additionally co-owns pending server-stream bootstrap/physical cleanup/supervision. Measure **dev→A and A→B separately**. No arithmetic subtraction of historical sampled object sizes, no inferred CPU fraction, and no claim of an overall 3.x win from a 64-byte deletion. This screen is not the user's final official-2.0.3 +10% QPS / −20% P99 acceptance.

Local B exact build: zero warnings/errors. 116 independent targeted tests passed. Full local suite: 2214 total, 2186 pass, 28 sandbox UDS/named-pipe failures, 0 skipped; all original failures retained. A separate reviewed hosted correctness gate must establish all 2214 expected case UIDs passed with zero skipped before this performance request may be activated. This document is NOT a trigger authorization.

## Reuse and narrow adaptations

Tool base `bff83aafa307c7e8c955a407c84c08075cdd4ea0`, tree `0c89b4a31914f3703c2d496566f7581eba9383bd` (reviewed W1). No existing file changes. Reuse original dependency/source/runtime verification, subprocess failure/timeout retention, exact-rational tick validation, and paired statistical summarizer. Timed probe is W1's probe with only its fixed-scene argument guard changed from 100-items/c1-or-c8 to 1-item/c1 and its unused preflight branch removed. A source regression test proves Operation, warmup/measurement/partial-tick loops and pass-through interceptor are byte-identical. ExactRank source unchanged. There is no new per-call observer, reflection, counter or scheduler mutation in the timed hot path.

The copied preparation entry adds only an explicit module-source input. Deadline-off module is byte-identical to the original. Deadline-on differs only by `[SharpLink.Sdk.Timeout(30)]` on the same server-stream method. Request default timeout remains disabled in both. Generated method deadline controls `observeEmission`; no deadline, cancellation, admission or user functionality is removed. All three products are fresh built/packed from their exact published sources; all probe/module/cold binaries and nonproduct DLLs must be byte-identical within each deadline scenario. Snapshot/source/loaded-assembly hashes and source stamps are checked, not merely relabeled.

A separate tiny cold executable performs fixed 128 natural one-item TCP RPCs per arm/deadline. It records the **actual generated descriptor**, looks at active supervisor registrations immediately after starting MoveNext but before awaiting it, and records actual bootstrap registration/task type names. Deadline-on must observe legacy `StartServerStreamingInvokerAsync` on dev/A and new `ServerStreamBootstrapCompletion` on B. Deadline-off must observe no pending bootstrap. All 128 payloads/order/EOF must validate, then client/server stop must drain bootstrap ownership. Failure to observe the required actual path fails preparation: no timing, no replacement preflight. No forced pump gate, ThreadPool limits, artificial timeout, latency or allocation metric. A cold observation proves the reachable mechanism, **not its frequency during the timed window**. The cold process is never reused for timing, and every timed process uses the same hashed product/module snapshot.

## Fixed bounded schedule

- 6 cold processes (dev/A/B × deadline off/on), all before timing.
- 48 fresh timed processes: 2 contrasts × 2 deadline scenarios × 6 pairs × 2 arms.
- Static generated channel with one pass-through CLIENT interceptor; 1 item/RPC, one worker, one shared loopback TCP connection, server synchronous yielding; no server interceptors. This is a fixed-per-call screening workload, deliberately not long streams, dynamic modules, other transports or all RPC kinds.
- Six rounds rotate the four contrast/deadline cells; within every cell AB/BA alternates by round. The schedule is generated before any measurement and every planned timed sample is attempted once, subject to the 60-minute global job cap (unattempted entries remain missing, not replaced). A is measured afresh in both contrasts; no shared A sample, no cross-contrast pooling or synthetic dev→B inference.
- Each process 3-second warmup, 6-second measurement. Default tiering/PGO/ReadyToRun, not a guaranteed Tier1 plateau. Runtime-only Core + ASP.NET 10.0.14, official archive SHA512 checks, exact roll-forward disabled; build SDK10.0.102.
- First two allowed logical CPUs selected once before measurement, same affinity throughout. Record complete available affinity, lscpu topology and runner image; do not infer separate physical cores, dedicated host or thread placement. No alternative CPU selection based on outcomes.
- One ordinary Ubuntu24.04 hosted job; all source builds, package checks, cold compilation and preflights before measurement marker. No concurrent build/trace/disassembly initiated by the tool. Shared-host contention remains possible; retain host observations and don't exclude noisy/unfavorable samples.
- Original summarizer: six paired ratios per cell; medians/ranges and arithmetic mean ratios; descriptive 20,000-resample bootstrap, seed529. RPCs are not independent experimental replications. QPS/P50/P99/P99.9/CPU/B-per-RPC all retained. No outcome threshold retrofitted, equivalence conclusion, release claim or native/code-size veto.

## Failure and publication boundary

No local timing has run. Product and tool source publication are authorized code work, not merge/release. REQUEST stays `review-required` until independent exact tool HEAD/tree and published source review, hosted correctness PASS, and root's one-dispatch authorization. One child commit changes only REQUEST, exact message `run: bootstrap-supervision paired diagnostic BS1`, branch `codex/bootstrap-performance-validation`, first run attempt only. Guard rejects extra paths/keys, dirty sources, wrong parents/source trees/ref/message/repository and reruns. No workflow_dispatch or re-run-to-green.

Expected authorized payload has keys `state`, `campaign`, `reviewedToolCommit`, `reviewedToolTree`, `sources`, `trees`, with exact pins above and campaign `bootstrap-supervision-48tcp-v1`. Publication via Git API may create a new commit identity; fetch/full-tree equality and exact request review precede triggering.

All timed failures, partial ticks when available, missing outputs, host/log records and all unfavorable cells remain. Timeout/crash may prevent tick flush; absence is reported, never zero. Preparation failures stop before timing and retain their logs. After output-path setup, workflow always uploads available evidence. Initial checkout/request guard failures precede that directory and may only retain Actions logs. No automatic replacement campaign or silent retry. Independent raw review is required for final interpretation even if the workflow succeeds.
