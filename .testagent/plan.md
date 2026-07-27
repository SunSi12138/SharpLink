# 0.8.30 regression-test plan

1. [x] Prove hosted Stop suppresses expected Run faults.
2. [x] Prove hosted Stop is a terminal barrier to later Start.
3. [x] Prove `Task<ValueTaskPayload>` is emitted with outer Task semantics.
4. [x] Prove public named-pipe/shared-memory address models reject invalid logical names.
5. [x] Prove local server health polling is allocation-free after warm-up.
6. [x] Run complete pre-fix Generator and Unit suites, preserving every existing pass and recording only the five new failures.
7. [x] Implement only proven fixes, then perform assertion-quality and pseudo-mutation reviews.
8. [x] Run exact-final-tree build, full tests, packages, fresh-cache smoke, documentation, and performance gates; create the local 0.8.30 commit.
