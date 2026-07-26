# 0.8.14 test status

## Pre-fix evidence

- The first full probe retained all 404 committed 0.8.13 tests and produced six failing cases: Unicode pipe length, two invalid instance-limit arguments representing one finding, producer callback escape, Client port zero, and a candidate later withdrawn after ownership review.
- The replacement flow-control finding is directly evidenced by the old global-FIFO branch and the inverted progress assertion: an eligible second stream remained incomplete while connection credit was available solely because the first stream had exhausted its own credit.

## Final gate

- Verified P2-or-higher improvements: 5/5.
- Assertion/pseudo-mutation review passed: reverting byte budgeting, either instance-limit boundary, callback isolation/reporting, any covered port-zero entry point, or stream-local waiter bypass is detected by the focused tests. Existing connection-credit FIFO coverage remains green.
- Reversed same-machine A/B found no stable latency or allocation regression. The post-identity-check candidate confirmation measured 21.61/45.40/140.52 ns at 0/48/272 B per operation.
- Non-incremental Release build passed with zero warnings and zero errors.
- Generator 83/83, Unit 411/411, and Integration 228/228 passed.
- Seven 0.8.14 packages were produced; a forced no-cache restore into a fresh package cache and the independent TCP, shared-memory, static-endpoint, and dynamic-resolver smoke all passed.
- Version, changelog, Chinese/English audit, migration, and performance documentation are complete. Final diff review passed and the tree is ready for the local commit.
