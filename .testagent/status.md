# 0.8.7 test status

## Pre-fix evidence

- Unit 371 total: both target regressions failed before production changes. Concurrent ClientConnection disposal returned before the blocked transport; Runtime Context retained only one of two Adapter scope failures. One unrelated shared-memory temporary-file contention failure will be rerun and is not counted.
- Unit 374 total: all three additional regressions failed before production changes. Concurrent Hosted Server Stop returned during blocked listener cleanup; connection close discarded Session failure after cancellation failure; a throwing cancellation callback prevented pending-call completion.
- Both target failures were recorded before their production fixes.

## Final gate

- Verified P2-or-higher improvements: 5/5 (ClientConnection disposal convergence; complete Runtime Context/Adapter cleanup diagnostics; Hosted Server stop convergence; connection-close terminal diagnostics; cancellation-safe pending-call teardown).
- Release build passed with 0 warnings/errors. Generator 83/83, Unit 374/374, Integration 228/228, package generation/analyzer verification, and package restore/run smoke passed.
- Isolated A/B measured normal ClientConnection disposal at 1.145 → 1.146 µs with overlapping 99.9% confidence intervals and unchanged 18.51 KB allocation. Two earlier allocating designs were rejected.
- Version 0.8.7 and Chinese/English audit, migration, performance, README, and changelog documentation are complete.
