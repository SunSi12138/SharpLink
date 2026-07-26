# 0.8.8 test status

## Pre-fix evidence

- Unit 379 total: exactly five new regressions failed against unchanged production code while the prior 374 tests passed.
- Anonymous-pipe teardown skipped the input pipe after output cleanup failed; shared-memory teardown left the mapping active after control cleanup failed.
- Dynamic-module release discarded its second service failure; multi-module shutdown discarded its second module failure; server-wide cleanup discarded the later static ownership failure.

## Final gate

- Verified P2-or-higher improvements: 5/5 (anonymous-pipe cleanup isolation; shared-memory mapping cleanup isolation; complete single-module diagnostics; complete multi-module diagnostics; complete server-wide ownership diagnostics).
- Release build passed with 0 warnings/errors. Generator 83/83, Unit 379/379, Integration 228/228, package generation/analyzer verification, and independent package restore/run smoke passed.
- Isolated A/B measured normal anonymous-pipe offer allocation/disposal at 2.590 → 2.592 µs with overlapping 99.9% confidence intervals and unchanged 2.13 KB allocation.
- Version 0.8.8 and Chinese/English audit, migration, performance, README, and changelog documentation are complete.
