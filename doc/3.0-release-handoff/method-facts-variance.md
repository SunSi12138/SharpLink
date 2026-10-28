# #732: diagnose phase drift before another performance campaign

Evidence: hosted run `40642190610`, tools `47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f`, three-arm fixed campaign. All six contrast/T-U decisions remain inconclusive. This appendix is a read-only analysis of retained samples, not a revised acceptance calculation. No samples were removed, no product changed, and no new RPC workload ran.

## The drift is broader than a single slow first sample

Each row below is the median across 36 processes of that cell's sample mean (whole-sample elapsed time / 2,000 calls), in microseconds per call. Samples are zero-based in raw JSON; the table labels them 1–5.

| Cell | Sample 1 | 2 | 3 | 4 | 5 | Processes with first slower than last |
|---|---:|---:|---:|---:|---:|---:|
| I/T | 148.95 | 162.17 | 161.93 | 162.43 | 151.84 | 18/36 |
| P/T | 117.87 | 122.14 | 105.67 | 95.58 | 87.97 | 35/36 |
| I/U | 83.59 | 78.93 | 75.06 | 75.27 | 74.47 | 33/36 |
| P/U | 69.11 | 70.35 | 68.00 | 68.45 | 67.52 | 25/36 |

The median within-process first/last mean ratio is 1.351 for P/T, 1.103 for I/U, 1.032 for P/U, and 1.001 for I/T. P/T's second sample is often still slow; deleting the first sample would neither resolve nor legitimately redefine the approved campaign.

All sample windows are short: **117.77–391.69 ms**, median **172.40 ms**. Whole fresh processes last **4.73–5.48 s**, median **4.98 s**. The 1,000-call warmup has no retained wall-clock boundary. The substantial subsequent P/T and I/U trajectories mean that this warmup did not establish an observed stable phase for those cells. There are no recorded JIT tier/code-version events, so tiering/PGO is a hypothesis, not an identified cause.

## Same-arm drift moves CPU and the middle of the distribution

Chronological second/first ratios; pair indices are zero-based. The P50 column compares the arithmetic means of the five sample P50s as a descriptive shape diagnostic, not a replacement for the campaign estimator.

| Same-arm pair | Processes | Cell | Whole-sample mean ratio | CPU/call ratio | Sample-P50 mean ratio |
|---|---|---|---:|---:|---:|
| AA12 | 24 → 25 | P/T | 0.8063 | 0.8137 | 0.7986 |
| BB14 | 28 → 29 | P/T | 0.8415 | 0.8390 | 0.8293 |

P/T sample means, microseconds per call:

- A24: **125.41, 109.11, 130.09, 100.66, 92.49**.
- A25: **95.28, 86.75, 90.69, 95.03, 81.97**.
- B28: **152.72, 125.45, 130.77, 94.66, 103.39**.
- B29: **113.50, 95.12, 116.39, 88.85, 96.94**.

This is not solely rare-tail inflation: CPU/call and central latency move in the same direction. Nor is it a uniform host speed multiplier: AA12 I/U and P/U mean ratios are 0.9908 and 1.0081; BB14 I/T becomes slower (1.0499) while P/T becomes faster (0.8415). Phase-specific runtime work and scheduling remain plausible.

## GC and scheduling observations limit attribution

Across measurement windows, recorded collection totals are **108 Gen0, 39 Gen1, 3 Gen2**. There are GC increments in 36 I/T samples and 72 P/T samples, and **none in any I/U or P/U sample**. The two highlighted pairs share the same P/T collection pattern: Gen0+Gen1 in sample 1, no collection in samples 2–4, Gen0 in sample 5. Different collection durations could still matter, but collection counts alone do not explain their approximately 16–19% mean shifts; I/U drift also occurs without recorded GC.

Individual call durations reach **5.969 ms**. Long calls exist both with and without collection increments. Preserve them; sample-level GC counts do not timestamp a pause or prove that GC caused a particular long call.

The 37 five-second environment observations show one-minute load **1.83–3.28**, CPU-pressure `some avg10` **3.23–61.19%**, and no recorded cgroup throttle periods/time (`nr_throttled=0`, `throttled_usec=0`). This indicates runnable-work pressure during portions of the run, not which request waited or whether the benchmark itself generated the pressure. Five-second observations are too coarse to align with 118–392 ms samples, and the raw rows lack exact sample-start/end timestamps. Do not infer a particular pause's cause or claim dedicated host CPUs from these records.

## Smallest next diagnostic: two M0 processes, no performance comparison

The next owner should propose and obtain authorization for this bounded **instrumented diagnostic**, not rerun O/A/B timing or change warmup until numbers look stable.

1. **Keep the source and execution conditions.** Use frozen M0 A, the same private .NET 10.0.11, default tiering/PGO, actual two-distinct-reported-core selection, and the original synchronous TCP service. Start exactly two fresh A processes. No cross-arm inference is needed to investigate drift already seen in A/A.
2. **Add a separate diagnostic mode in the tool only.** Preserve four-cell order and the real invocation path. Execute 1,000 initial calls plus five fixed 2,000-call chunks per cell. Emit an EventSource phase marker at process/cell start, warmup completion, and each chunk boundary, carrying cell, chunk, completed count and monotonic timestamp. Do not emit throughput, latency ratios or performance acceptance. Keep reflection and tracing out of product/service code.
3. **Capture runtime events from process startup.** Use the installed `dotnet-trace` version's launch/diagnostic-port mode so startup JIT is not missed. Record its version, help/options, exact providers and command. Collect CLR JIT/method-load/code-version, GC start/stop and ThreadPool events together with the phase EventSource. Retain `.nettrace`, stderr and exit status. Map actual generated Facts proxy/stub and the server dispatch/stream-pump methods; on A also map `RpcMethodFacts.ToDescriptor`. A missing standalone body may be inlined: inspect the actual caller rather than treating absence as zero work.
4. **Capture scheduling with an explicitly capable tool.** On a disposable Linux .NET 10 runner with granted root access and supported kernel, use `dotnet-trace collect-linux` with its `thread-time` profile and runtime/phase providers. This supplies native/kernel scheduling context that ordinary EventPipe samples do not. If those prerequisites are unavailable, retain the EventPipe diagnostic and mark kernel scheduling attribution unavailable; do not silently call sampled stacks a context-switch trace. Do not change host permissions to bypass a denial.
5. **Produce one event-aligned table, not a benchmark chart.** Output `diagnostic-manifest.json` (source, binaries, runtime, CPU topology, commands), `phases.jsonl`, original trace(s), `method-code-versions.csv` (method/code version/tier/time/phase), `gc-pauses.csv`, and `phase-runtime-summary.csv` (JIT activity, GC intervals, ThreadPool changes, runnable/off-CPU evidence where captured). Include first and last event coverage and losses, so absent events are not mistaken for absent activity.

### Decide from the trace

- Optimized code versions appearing after warmup, especially during declining P/T or I/U chunks, support a tier-transition explanation; identify the actual methods and overlap. They do not establish how much of the old timing difference was caused by JIT.
- Stable relevant code versions with matching GC or runnable/off-CPU stalls instead support those alternatives. Without a kernel scheduling trace, keep scheduler attribution unresolved.
- If the required methods, tiers or phase alignment are missing, report that exact collection gap and repair only the diagnostic. Do not add timed pairs or delete a cell.
- Instrumentation changes execution. These traces explain possible mechanisms; their durations do not replace the frozen experiment's mean/P99 or clear NoGo. Only after this bounded diagnosis should the owner propose a revised warmup/order/measurement plan for independent approval.

The approved three-arm results remain intact and inconclusive. A nanosecond projection microbenchmark cannot explain or repair the observed phase-dependent network drift by itself.
