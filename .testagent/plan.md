# 0.8.33 regression-test plan

1. [x] Prove inherited same-parameter RPC declarations with incompatible returns are silently collapsed and produce an invalid Proxy contract.
2. [x] Prove distinct enum type names can sanitize to the same generated Stub size-field identifier.
3. [x] Prove synchronous Builder rollback deadlocks an async resource cleanup that captures a non-pumping synchronization context.
4. [x] Prove duplicate Client Hosted Start cleans up the previously owned instance.
5. [x] Prove duplicate Multi-Cluster Hosted Start cleans up the previously owned coordinator.
6. [x] Run complete pre-fix Generator and Unit suites, preserving every existing pass and recording only the new failures.
7. [x] Implement only proven fixes, then perform assertion-quality, pseudo-mutation, and performance reviews.
8. [x] Run exact-final-tree build, full tests, packages, fresh-cache smoke, documentation, and performance gates; create the local 0.8.33 commit.
