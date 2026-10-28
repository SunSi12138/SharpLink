# Phase rewrite: frozen diagnostic performance plan

The user explicitly requests actual performance of the already-correct phase
rewrite. Native footprint growth is NOT an acceptance or launch gate. Existing
mechanism completeness failures remain disclosed, not zero-cost or product errors.
This campaign decides whether the rewrite improves this bounded TCP workload;
none of its outcomes alone authorizes merge/release or lifts #931 NoGo.

## Frozen identities

- Pre-rewrite full-product baseline: `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`.
  **This is not official 2.0.3.** Official 2.0.3 remains the ultimate performance
  baseline and is outside this first isolated-change diagnostic.
- Original candidate: `6077617167adbfd561e494d733fac2e1779f218f`.
- Published source-identical backup: `2153cd9228c1de25e89a4bb68f0e3da4dc19f867`, full tree
  `02a2824f344c3614fff8de1d8a10ed7a5be87c21`. Loaded DLL source stamps must use the
  actually built published SHA, never relabel it as original 607. Candidate tree
  equality is checked before build. Both arms are coherent full-product builds.
- Reuse reviewed full-product tools at `271103147577a70bd973adfac1860f2da2276f07`
  (local reviewed tree 1712b82), inheriting original `9b11cf3b` measured operation.
  New phase entry/helper/validator are copies changing identities/runtime only;
  legacy TCP probe, preparation, schedule, summarizer and scalar sources are unchanged.
  A phase-specific scalar copy changes only pre-loop representation discovery: use
  the async-iterator attribute or explicit nested Enumerator, then IAsyncStateMachine
  interface-map MoveNext. Its runner/project and entire allocation-call loop are
  byte-identical to the original. A layout-only flag supports non-timed adapter checks.
- Build SDK10.0.102; private runtime-only .NET and ASP.NET10.0.14, exact framework
  pins and rollForward Disable. SHA512 official artifact pins are those independently
  verified for mechanism tool4f2c1751. Download metadata and archives, retain hashes.

## Execution: one hosted job, no local timings

Single standard ubuntu24.04 runner, first two allowed logical CPUs selected once
(not dedicated cores). Retain topology/CPU/OS/runtime/SDK and before/after host
observations. Default tiering/PGO/ReadyToRun; clear inherited JIT overrides using the
unchanged driver. No claim that all calls reach Tier1 or native completeness.
Build both clean products and fresh-pack/verify nine packages, build probes once
against baseline, copy byte-identical probe/module and non-product dependencies,
check snapshot/loaded DLL hashes and source versions, pin runtimeconfigs, shut down
build servers, then mark no-more-build boundary. No build during measurement.

Exactly48 fresh TCP processes: static/dynamic intercepted ×1/100 items ×6 pairs.
One worker, closed-loop one outstanding RPC, pool1/1, exactly one ready connection,
no request timeout, heartbeat3600s, one passthrough Client interceptor, no Server
interceptor. Service yields synchronously (`await Task.CompletedTask`); client
has natural asynchronous TCP I/O. This is NOT forced-async, all deadlines/failures,
or all concurrency/load shapes. No per-call instrumentation is added.

For rounds1..6, rotate original item-major scene list
[static1,dynamic1,static100,dynamic100] left by(round-1)%4; every scene uses AB in
odd rounds, BA in even rounds. Each fresh process: unchanged3s warmup+6s measured.
Stop on first process failure; retain all attempted raw/log/host/tick evidence and
explicit missing slots, no retry/replacement/extra warmup/additional pair.

After TCP, original14-process scalar schedule with adapted layout discovery: five FullOpts paired processes
plus one pair default-tiered and one pair PGO-off. Each contains static/dynamic
×0/1/100 ×no deadline/five-minute declared deadline; 2000 warmup calls and20000
synchronously completing calls/row. These establish allocation/layout observations,
NOT synchronous-path throughput/latency. No scalar/native size gate on TCP results.

## Metrics and interpretation fixed before timing

Independent process pair is the statistical sample unit, six pairs per cell;
RPC durations are not independent process replications. Report all four cells,
QPS, CPU us/RPC, P50/P99/P99.9 and allocated B/RPC; retain per-process values,
medians/ranges, six candidate/baseline paired ratios, ratio of medians, arithmetic
mean paired ratio and original descriptive bootstrap(seed529,20000 replicates,
indices499/19499). Raw duration arrays are exported only after measured window.
Zero errors required for usable samples: every item/order/count/sum is checked,
wrapper/channel identities and single ready connection are checked, process must
exit0 including client/server shutdown. These do not replace comprehensive ownership,
CTS, retention, fault or full release correctness gates; prior17 differential+109
adjacent tests remain separate source-applicable evidence.

No new perf pass/fail or equivalence margin. Missing/invalid/failed samples imply
incomplete evidence, not passing performance. Coherent favorable direction supports
further research; adverse cells are reported; mixed or interval-crossing results
are inconclusive, NOT equivalence/no regression. Do not pool with historical runs,
select favorable cells, loosen thresholds or rerun to green. Report native gaps
separately; code/body size is neither the reason to stop nor a proxy for timings.

Opened-only Draft DO NOT MERGE PR on base
`codex/931-r1-full-product-validation` at2711031, head
`codex/phase-rewrite-performance-validation`; read-only contents permission,
90minute timeout, always-upload all evidence. Exact plan/tool review before opening;
verify remote tree/base first. Ordinary inherited PR CI is not this product's full
correctness acceptance. No merge/tag/release/security/permission change.
