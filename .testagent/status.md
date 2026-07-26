# 0.8.5 test status

## Pre-fix evidence

- Unit 360 total: all three new regression tests failed before production changes. The stopped-client race returned a client on attempt 16; activation rollback retained only scope cleanup failure; lease cleanup retained only service disposal failure.
- Unit 362 total: the initial-pool rollback regression failed before its fix because cleanup replaced the second connection failure; inspection also confirmed the client remained in `Connecting` and rollback stopped at the first cleanup failure.
- Unit 363 total: the leased-invocation regression failed before its fix because the handler failure was retained but the lease cleanup failure was absent.
- The failures were recorded before their production fixes.

## Final gate

- Verified P2-or-higher improvements: 5/5 executable proofs recorded (atomic client publication/stop; activation rollback diagnostics; complete layered service cleanup diagnostics; complete fixed-client initialization rollback; complete leased-invocation terminal diagnostics).
- Generator 83/83, Unit 364/364, Integration 228/228, Release build with 0 warnings/errors, package generation, SDK analyzer-content verification, and package restore/run smoke passed.
- Same-machine BenchmarkDotNet A/B measured published-client accessor lookup at 1.457 → 1.483 ns with overlapping 99.9% confidence intervals and 0 B/op on both versions.
- Version 0.8.5 and Chinese/English audit, migration, performance, README, and changelog documentation are complete.
