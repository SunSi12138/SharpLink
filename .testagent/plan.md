# 0.8.39 regression-test plan

1. [x] Prove Server interceptors cannot observe terminal failures before unwind.
2. [x] Prove response-bearing Server interceptors can silently omit the terminal.
3. [x] Prove wrong Client short-circuit types publish Succeeded before caller failure.
4. [x] Prove Client stream enumeration captures a supplied synchronization context.
5. [x] Prove generated malformed request shapes are misclassified instead of DataLoss.
6. [x] Run complete pre-fix affected suites and preserve all existing passes.
7. [x] Implement only proven fixes and complete assertion/pseudo-mutation review.
8. [x] Run non-incremental Release and complete Generator/Unit/Integration gates.
9. [x] Run exact-baseline performance, Chaos, NativeAOT, and package gates.
10. [x] Update bilingual 0.8.39 documentation and commit locally.
