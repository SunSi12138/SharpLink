# 0.8.9 test status

## Pre-fix evidence

- Unit 384 total: exactly five new regressions failed against unchanged production code while the prior 379 tests passed.
- Shared-memory control disposal returned after stream failure while its reader was still blocked; single-client and multi-cluster Hosted Stop callers returned before the winning cleanup.
- Concurrent listener disposal returned before queued cleanup, and one queued connection failure prevented a later connection from being disposed.

## Final gate

- Verified P2-or-higher improvements: 5/5 (control-reader convergence after cleanup failure; single-client Hosted Stop convergence; multi-cluster Hosted Stop convergence; asynchronous listener disposal convergence; queued listener cleanup isolation).
- Release build passed with 0 warnings/errors. Generator 83/83, Unit 384/384, Integration 228/228, package generation/analyzer verification, and independent package restore/run smoke passed.
- Isolated A/B measured normal anonymous-pipe offer allocation/disposal at 2.576 → 2.597 µs with overlapping 99.9% confidence intervals and unchanged 2.13 KB allocation. An earlier unconditional Task/lock design was rejected at 2.19 KB.
- Version 0.8.9 and Chinese/English audit, migration, performance, README, and changelog documentation are complete.
