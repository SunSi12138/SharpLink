# 0.8.13 test status

## Pre-fix evidence

- The initial full pre-fix Unit run executed 404 tests and failed four retained regressions plus a mapping hypothesis; all 399 tests from the committed 0.8.12 baseline passed. Ownership review withdrew the mapping product change rather than overstate its evidence.
- Its replacement notification-state regression then failed alone against the candidate before read-operation ownership was added (403/404), demonstrating that rejected-read cleanup could strand the accepted read after data arrived.

## Final gate

- Verified P2-or-higher improvements: 5/5.
- Full Unit is 404/404 after the fixes. Non-incremental Release build has 0 warnings/errors; Generator is 83/83 and Integration is 228/228.
- Pseudo-mutation review: omitting the final writer join leaves disposal complete while the controlled writer is live; removing the token registration strands the wait and active read; removing read-operation ownership clears the accepted read's notification flag; removing flush convergence lets completion precede the active spill flush. Each mutation is killed by a distinct state assertion.
- Assertion review: the five tests assert externally relevant ownership, task-liveness, cancellation type, or completion ordering. The controlled blockers are released after observing the failure state, so tests cannot pass by hanging; no new test is assertion-free or trivial-only.
- Reversed steady-state A/B found no regression: available-data Reader read/advance stayed about 71-73 ns at 0 B/op, default-token control waits stayed about 20 ns at 0 B/op, and normal writer completion stayed in the same band while allocation fell 280 to 256 B.
- Version 0.8.13, Chinese/English audit, migration, performance, README, changelog, seven packages, independent restore, and TCP/shared-memory/static/dynamic endpoint smoke are complete.
