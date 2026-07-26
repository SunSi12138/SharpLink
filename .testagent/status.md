# 0.8.17 test status

## Evidence status

- Five P2-or-higher candidates were identified from the clean `0.8.16` commit `0e4e1a7`.
- The complete pre-fix Unit run executed 427 cases: all 422 prior cases passed and exactly five focused probes failed.
- The probes directly observed two child unregister calls and a replaced failure, a shared/dropped TLS chain policy, accepted inconsistent capability sets, mutable live partition limits, and accepted unbounded sizing configurations.

## Current gate

- Verified P2-or-higher candidates with failing evidence against `0e4e1a7`: 5/5.
- All five production fixes pass the complete 427-case Unit suite.
- Assertion and pseudo-mutation review confirmed that each probe fails if its corresponding guard, clone, bound, or shared-operation coordination is removed.
- Four counterbalanced A/B pairs show unchanged allocations and no stable latency regression on buffer-pool, pending-table, flow-control, handshake, runtime-context, and server-lifecycle paths. The deliberate cold-path snapshot costs are 88 B for TLS options and 72 B for admission-controller creation.
- Version, changelog, README, and Chinese/English audit, migration, and performance documentation are complete.
- Non-incremental Release build passed with 0 warnings and 0 errors; Generator 83/83, Unit 427/427, Integration 228/228, seven-package pack, and fresh-cache package smoke all passed.
- Targeted formatting, whitespace validation, final tests, and diff review passed; the 0.8.17 batch is ready for its local commit.
- Consecutive complete audit rounds without a new improvement: 0/3.
