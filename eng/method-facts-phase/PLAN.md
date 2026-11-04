# Frozen M0 phase diagnosis, not another performance campaign

Historical three-arm run40642190610 remains inconclusive; M0 NoGo and M1 Hold are unchanged. The evidence branch retains those samples. This follows the diagnostic recommended by `ba99c6f9467fd85233afbc0dc1c84cf839e1b5a0:doc/3.0-release-handoff/method-facts-variance.md` rather than repeating timing, changing warmup, or comparing different ABIs.

Freeze original M0 `37548b7a35d50803212d2b0a637306636cc0a1d3`, original tools base47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f, SDK10.0.102, private runtime10.0.11. Preserve original synchronous service and RunCalls method verbatim. Diagnostic only changes tool orchestration: four cells I/T,P/T,I/U,P/U; each1000 warmup+5x2000 completed one-item TCP calls, concurrency1, same two distinct reported physical cores. Exactly two fresh diagnostic processes,44000 calls each. Original stopwatch call in RunCalls is retained for source parity but no tick arrays, latency/throughput ratios or performance judgment are emitted. Instrumentation affects behavior and is not a benchmark.

Dotnet-trace10.0.750501 launches each probe suspended from startup. Providers: CLR0x1000010019 level5 (GC,loader,JIT,threading,compilation), SharpLink-MethodFacts-Phases all keywords level5. Phase markers are only outside call loops. Runtime binary identities, loaded product MVIDs, exact host/CoreLib, topology, commands, trace hashes and coverage/loss data are retained. Missing standalone method bodies are unresolved, possibly inlined, not zero work. Method-load rundown observations are not JIT timestamps.

No privilege escalation, host tracing permission changes or kernel collection is attempted. Kernel scheduler attribution remains unavailable. Ordinary EventPipe cannot prove runnable/off-CPU causes. Retain phase-correlated GC/JIT/ThreadPool observations without causal percentage attribution.

All build/tool setup ends before either process. Each process has one180-second attempt. First nonzero/start/timeout/integrity failure stops and preserves completed/partial state; no extra samples/retries. Only owned process groups can be terminated and reaped. Both processes must retain54 sequential markers and44000 calls; missing trace phases/runtime events are reported as collection gaps, not performance recovery.

Raw trace/log files remain local on the disposable runner; no upload-artifact or raw-log publication. Only bounded summary statistics and identities enter normal CI output. After runner disposal raw traces are unavailable; this retention limitation must accompany conclusions. User's no-raw-upload preference prevails over historic full-trace archiving suggestion.

Product source, Cancel regression, D2 operations and performance thresholds are unchanged. No merge, release or deletion. Independent review of final tool source and this plan precedes workflow publication/dispatch.

Instrumentation difference: the diagnostic passes null instead of a per-call ticks array, removing the original timestamp-store branch and array allocation. Phase events/JSON I/O also change scheduling. The underlying RPC calls and validation loop remain source-identical, but diagnostic phase durations are not directly comparable with old measurement windows. No old sample is replaced by trace duration.
