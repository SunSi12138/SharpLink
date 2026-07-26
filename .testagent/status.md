# 0.8.4 test status

## Pre-fix evidence

- Unit 351 total: the three deterministic codec races fail before the fix. Generated resolution returns generation 1 after generation 2 publication; fallback resolution returns fallback after a generated registration is published; in-flight resolution returns successfully after context disposal.
- Unit 353 total: asynchronous retained-frame replay blocks registration for more than 200 ms, and a reentrant dispatcher configuration callback observes the request registry lock held for more than 200 ms.
- Integration 228 total: one deterministic replacement test fails because a child that commits the new assembly and then reports old-generation cleanup failure leaves the multi-cluster coordinator routed to the removed generation.
- All six deterministic failures were recorded before the corresponding production fixes.

## Final gate

- Verified P2-or-higher improvements: 5/5 executable proofs recorded (atomic codec publication; dispose/resolution exclusion; nonblocking pre-admission replay; callback lock isolation; replacement-state reconciliation).
- All focused regressions pass, including replay ordering and completion during asynchronous replay.
- Release build passed with 0 warnings and 0 errors. Generator 83/83, Unit 357/357, and Integration 228/228 passed.
- Performance A/B: cached explicit/fallback lookups 6.529 → 6.533 ns and 6.515 → 6.504 ns; cached generated lookup 8.670 → 6.499 ns; attached pre-admission dispatch 17.656 → 17.098 ns. All remained 0 B/op. The first replay design's 19.755 ns result was rejected before release.
- Version and Chinese/English audit, migration, performance, README, and changelog documentation are updated to 0.8.4.
