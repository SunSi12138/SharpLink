# 0.8.36 regression-test plan

1. [x] Prove the Server admission/Stop global-count race and counter invariants.
2. [x] Prove Server Stop does not join connection-scoped asynchronous cleanup.
3. [x] Prove profile defaults overwrite an explicitly assigned 8 MiB queue.
4. [x] Prove the public per-call compression switch is unusable and define its removal boundary.
5. [x] Prove handshake response compression/profile incoherence crosses public codec boundaries.
6. [x] Run complete pre-fix suites and preserve all existing passes.
7. [x] Implement only the proven fixes and review assertions/pseudo-mutations.
8. [ ] Run exact-final build/tests/performance/Chaos/AOT/packages/fresh-cache smoke and commit 0.8.36 locally.
