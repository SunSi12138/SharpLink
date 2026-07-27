# 0.8.34 regression-test plan

1. [x] Prove shared-memory reader completion can release staging/mapping ownership while a read operation is still pending but has not published a ReadResult.
2. [x] Prove the Chaos release gate can report Passed even after its client logger captures an Error.
3. [x] Prove inherited identical RPC signatures with conflicting Oneway call shapes are silently collapsed.
4. [x] Prove inherited identical RPC signatures with conflicting timeout/idempotency policies are silently collapsed.
5. [x] Prove inherited identical RPC signatures with conflicting parameter name/nullability schemas are silently collapsed.
6. [x] Run complete pre-fix Generator and Unit suites plus the bounded Chaos oracle probe, preserving every existing pass and recording only new failures.
7. [x] Implement only proven fixes, then perform assertion-quality, pseudo-mutation, Chaos, NativeAOT, and performance reviews.
8. [x] Run exact-final-tree build, full tests, packages, fresh-cache smoke, documentation, and performance gates; create the local 0.8.34 commit.
