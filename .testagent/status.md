# 0.8.18 test status

## Evidence status

- Five P2-or-higher candidates were identified from the clean `0.8.17` commit `f7d4b8d`.
- The complete pre-fix Unit run executed 432 cases: all 427 prior cases passed and exactly five focused probes failed.
- The probes directly observed a lost Hosted Client owner, an early failed module drain, a faulted send pump, accepted `int.MaxValue` Server call concurrency, and interrupted sibling stream/transport cleanup.

## Current gate

- Verified P2-or-higher candidates with failing evidence against `f7d4b8d`: 5/5.
- All five production fixes pass the complete 432-case Unit suite.
- Assertion and pseudo-mutation review confirmed that each probe independently detects removal of owner disposal, delay slicing, stopwatch saturation/timer slicing, the concurrency bound, or resilient dispatcher draining/Session suppression.
- Four counterbalanced A/B pairs retain 0/48/0 B per operation on buffer-pool, pending, and flow-credit controls with no stable latency regression. Empty Session, Runtime Context, and Server lifecycle allocations are unchanged.
- Resilient two-stream terminal draining intentionally adds one 32 B snapshot allocation so callbacks run outside the request lock and every entry is detached; this is a bounded shutdown-path cost.
- Version, changelog, README, and Chinese/English audit, migration, and performance documentation are complete.
- Non-incremental Release build passed with 0 warnings and 0 errors; Generator 83/83, Unit 432/432, Integration 228/228, seven-package pack, and fresh-cache package smoke all passed.
- Targeted formatting, whitespace validation, final tests, and diff review passed; the 0.8.18 batch is ready for its local commit.
- Consecutive complete audit rounds without a new improvement: 0/3.
