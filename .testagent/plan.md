# 0.8.32 regression-test plan

1. [x] Prove bound Unix-socket cleanup with no captured identity deletes a replacement.
2. [x] Prove provider `WireProfile` remains mutable after runtime Build.
3. [x] Prove an undefined authentication rejection code faults the handshake instead of returning a stable rejection.
4. [x] Prove a positive `TimeSpan.MaxValue` default request timeout fails before a request is sent.
5. [x] Measure and gate the three recurring framework-owned arrays on immediate server admission.
6. [x] Run complete pre-fix Unit and Integration suites, preserving every existing pass and recording only the five new failures.
7. [x] Implement only proven fixes, then perform assertion-quality and pseudo-mutation reviews.
8. [x] Run exact-final-tree build, full tests, packages, fresh-cache smoke, documentation, and performance gates; create the local 0.8.32 commit.
