# 0.8.10 test status

## Pre-fix evidence

- Unit 389 total: exactly five new regressions failed against unchanged production code while the prior 384 tests passed.
- Fixed-endpoint and profile-binding rollback discarded transport cleanup failure; Manifest and Context construction discarded earlier Scope cleanup failure.
- Client construction let Runtime Context cleanup replace the original option/build validation failure.

## Final gate

- Verified P2-or-higher improvements: 5/5 (fixed-endpoint rollback diagnostics; profile-binding rollback diagnostics; Manifest preparation rollback diagnostics; Context construction rollback diagnostics; Client/Context rollback diagnostics).
- Release build passed with 0 warnings/errors. Generator 83/83, Unit 389/389, Integration 228/228, package generation/analyzer verification, and independent package restore/run smoke passed.
- Alternating isolated A/B measured normal Runtime Context build/disposal at 346.1 → 343.7 ns with unchanged 3.9 KB allocation. An early 350.2 ns candidate was moved to a no-inline cold rollback path.
- Version 0.8.10 and Chinese/English audit, migration, performance, README, and changelog documentation are complete.
