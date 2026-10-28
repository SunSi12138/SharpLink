# Experimental fenced write-elision

Base: f724c7aa. One 11-line product change in WakeupSignal.Signal. No hot-path state/counter/query, API, wire, ordering, scheduling preference or physical-ownership changes.

- `PUBLICATION-PROOF.md`: all-caller/consumer publication argument, required independent review and limits.
- `wakeup_litmus.py`: exhaustive bounded abstract store-buffer model, NOT a CLR runtime or timing benchmark.
- `wakeup_litmus_result.json`: original/fenced-reread have no lost-wake terminal in this model; unfenced and fence-without-reread each have a counterexample.
- Tests add repeated-latch no-write coverage and clear/recheck/timed-rearm concurrent mailbox coverage. Existing tests cover live-arm claim, stale timers, late latch/arm crossing, context and global asynchronous continuation ownership.

Status: product code/testing is experimental and unpublished. Actual non-timed test results and emitted-code coverage will be added when the serial runtime window becomes available. No comparative performance run is authorized here; no target claim.

Additional offline SC model: `wakeup_sc_model.py` separately explores two producers, a timed generation-1 callback and an untimed generation-2 rearm, with claim and result publication as separate actions. Original: 1,416 states; candidate: 1,294; both 27 terminal states, no duplicate/stale-core completion assertion failure. It deliberately does not replace the publication weak-memory argument.

## First correctness/native observation

Commit 2e2c77d: Release build 0 warnings/errors, project-reference guard and maintainability gate passed; 88/88 focused tests passed. Results retained at `artifacts/attempts/001-thread-wrapper`. Native observation is **Instrumented Tier0 only**, Signal body592 bytes; Thread.MemoryBarrier is a wrapper call, so no actual barrier opcode or optimized cost is established by that capture. Candidate758de8e uses the direct Interlocked.MemoryBarrier spelling; exact-head retest/native observation is pending the next serialized runtime window. Neither the first tests nor bounded models prove the full ARM64/runtime protocol.

Final non-timed observation: direct-intrinsic product758de8e, tested at documentation commitb49fe3f, passed all88 tests and both guards again. Actual x64 Instrumented Tier0 now contains `lock or dword ptr [rsp],0` before the fresh readiness read. `RESULTS.md` and `artifacts/evidence/manifest.json` record exact coverage, retained first iteration and outstanding Tier1/ARM/performance/independent-review requirements.
