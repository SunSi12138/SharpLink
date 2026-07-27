# 0.8.34 test status

## Evidence status

- Five version-advancing P2 candidates identified against clean 0.8.33 commit `35c8cd2`, plus two P2 follow-up findings and one deterministic-test correction included without advancing another version.
- Shared-memory Chaos evidence: 419,817 successful operations, 152,238 injected failures, 11 restarts, two client-background NREs, but a false `Passed` result.
- TCP cross-check: 205,321 successful operations, 74,251 injected failures, five restarts, and no ClientErrors, isolating the runtime race to shared memory.
- NativeAOT independent-process shared-memory smoke passed before changes.
- Pre-fix Generator ran 107 tests: all 104 existing tests passed and exactly the three new inherited-semantics probes failed.
- Pre-fix Unit ran 478 tests: all 477 existing tests passed and exactly the new pending-read completion probe failed.
- The deterministic Chaos Error injection completed 602 successful calls and two restarts, retained the injected Error in `ClientErrors`, but exited 0 with `Status=Passed`.
- The first fixed-oracle run exposed two previously invisible terminal StreamPipeReader `AdvanceTo` errors in addition to the injected Error; this sixth P2 is included in 0.8.34 without advancing another version.
- The first final 120-second rerun correctly failed on one recoverable shared-memory expansion handshake interruption that was misclassified as an unhandled Error. A deterministic Unit probe reproduced the classification, and fixed/static/dynamic expansion and reconnect paths now emit Warning event 6101 while real background faults retain Error 6002.
- The corrected 120-second rerun passed with 863,299 successful calls, 310,349 injected failures, 11 restarts, zero unexpected failures, no client Error logs, a successful drain, and every final active metric at zero.
- NativeAOT independent-process shared-memory smoke passed with `AOT_SMOKE_CLIENT_PASS`.
- Reader A/B measured 29.564 ns / 40 B at baseline and 30.046 ns / 40 B for the final guarded reader (+1.63%). An earlier +7.9% design was rejected.
- Final inherited-Generator alternating-order A/B produced process medians 17.084/17.853 ms at baseline and 15.806/17.497 ms for the candidate; the median-of-medians improved 17.469 -> 16.652 ms and allocation improved 30,720,156 -> 30,654,364 B. Earlier +39.9% and unstable repeated-attribute-scan designs were rejected.
- Exact-final-tree non-incremental Release build passed with zero warnings/errors; Generator 108/108, Unit 478/478, and Integration 238/238 passed.
- Seven 0.8.34 packages were produced; the SDK package contains the Generator analyzer, and a unique-cache consumer compiled a generated contract and loaded all seven assemblies as 0.8.34.
- Consecutive complete audit rounds without a new improvement reset from 1/3 to 0/3.

## Current gate

- Research, pre-fix evidence, assertion/pseudo-mutation review, implementation, Chaos, NativeAOT, performance, documentation, exact-final-tree suites, and package gates are complete.
- The tracked tree is the reviewed 0.8.34 local-commit candidate.
