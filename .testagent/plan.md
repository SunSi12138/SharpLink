# 0.8.42 regression-test plan

1. [x] Prove Throughput timed batching can terminate the process under streaming load.
2. [x] Prove Memory/ReadOnlyMemory accept an invalid null collection marker.
3. [x] Prove nullable fixed Codecs accept non-canonical ignored null bodies.
4. [x] Prove local control and handshake writers misclassify invalid values as peer violations.
5. [x] Prove DTO member nullability is absent from runtime Codec schema identity.
6. [x] Implement only the five proven fixes with compatibility controls.
7. [x] Run non-incremental Release and Generator/Unit/Integration gates.
8. [x] Run exact-baseline performance, Chaos, NativeAOT, and package gates.
9. [x] Update bilingual 0.8.42 documentation and complete local-commit readiness review.
