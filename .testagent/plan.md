# 0.8.44 regression-test plan

1. [x] Prove and fix three material, independent root causes.
2. [x] Reclassify repeated manifestations of the shutdown-join defect as one finding.
3. [x] Close this audit round without padding the batch with additional call sites,
   defensive-only changes, theoretical races, or syntax modernization.
4. [x] Run non-incremental Release and Generator/Unit/Integration gates.
5. [x] Run exact-baseline performance, Chaos, NativeAOT, and package gates.
6. [x] Update bilingual 0.8.44 documentation and reach local-commit readiness.
7. [ ] Begin the next whole-framework audit; stop after three consecutive clean rounds.
8. [ ] Recluster all historical findings by independent engineering root cause and write the
   final audit report outside the repository on the Desktop.
