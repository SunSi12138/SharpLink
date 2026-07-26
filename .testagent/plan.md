# 0.8.15 regression-test plan

1. [x] Add a real-filesystem regression proving Unix listener construction preserves a pre-existing ordinary file.
2. [x] Add a real-loopback regression proving socket factories retain their construction-time endpoint.
3. [x] Add frozen-configuration regressions for built-in socket, TLS, and shared-memory endpoint delegates.
4. [x] Add direct Client transport/resolver ownership regressions for a second build.
5. [x] Add Server listener ownership regressions for a second build and failed-build rollback.
6. [x] Run the pre-fix evidence set, implement only proven fixes, then complete assertion/pseudo-mutation review and performance A/B.
7. [x] Complete non-incremental build, full tests, package smoke, documentation, and local 0.8.15 commit.
