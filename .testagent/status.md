# 0.8.33 test status

## Evidence status

- Five P2 candidates identified against clean 0.8.32 commit `2f3d27c`.
- The pre-fix Generator suite ran 104 tests: all 102 existing tests passed and exactly the two new Generator probes failed.
- The pre-fix Unit suite ran 477 tests: all 474 existing tests passed and exactly the three new lifecycle/rollback probes failed.
- All five fixes now pass strengthened assertions. Pseudo-mutations restore a broken artifact, duplicate field, context deadlock, disposed owner, or poisoned accessor and are caught by the new tests.
- A 40-contract/400-enum-method Generator A/B measured 20.192 ms / 32,888,392 B at 0.8.32 and 15.116 ms / 33,142,168 B for the final design. A SHA-256 identifier design that raised allocation by about 5.1% was rejected; the retained deterministic 64-bit suffix limits the stress-fixture increase to 0.77% with no latency regression.
- Exact-final-tree Release build completed with zero warnings/errors; Generator 104/104, Unit 477/477, and Integration 238/238 passed.
- Seven 0.8.33 packages were created, the SDK package contains its Generator analyzer, and a fourth unique fresh package cache restored, compiled generated code, and ran all seven assemblies at version 0.8.33.
- Consecutive complete audit rounds without a new improvement: 0/3.

## Current gate

- Research, test mapping, and full pre-fix evidence are complete with a zero-warning non-incremental build.
- All 0.8.33 engineering gates are complete. The local version commit is the only remaining action.
