# 0.8.37 regression-test plan

1. [x] Preserve raw compiler evidence for inaccessible generated service artifacts.
2. [x] Prove service/DTO reachability diagnostics across rejected and allowed accessibility.
3. [x] Prove keyword DTO members produce valid distinct local/member identifiers.
4. [x] Prove unsealed record DTOs cannot silently slice derived state.
5. [x] Prove ref-like DTOs are diagnosed and suppress broken contract artifacts.
6. [x] Prove static abstract operators/conversions are diagnosed and suppress broken Proxies.
7. [x] Run the complete pre-fix Generator suite and preserve all existing passes.
8. [x] Implement only the five proven fixes and review assertions/pseudo-mutations.
9. [x] Correct the load-only admission race-probe false positive and pass consecutive Unit reruns.
10. [ ] Run exact-final build/tests/performance/Chaos/AOT/packages/fresh-cache smoke and commit 0.8.37 locally.
