# 0.8.35 regression-test plan

1. [x] Prove retried Resolver failures are emitted as unhandled Errors.
2. [x] Prove an injected Server Error is invisible to the Chaos release gate.
3. [x] Prove an explicitly unwritable Chaos JSON output can exit zero.
4. [x] Prove protocol teardown can join reader completion while retaining the active read.
5. [x] Measure internal Runtime Context profile-read allocation against exact 0.8.34.
6. [x] Run complete pre-fix suites/probes and preserve all existing passes.
7. [x] Implement proven fixes and review assertions/pseudo-mutations.
8. [ ] Run exact-final build/tests/Chaos/AOT/packages/fresh-cache smoke and create the local 0.8.35 commit.
