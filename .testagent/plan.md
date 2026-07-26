# 0.8.12 regression-test plan

1. [x] `DirectClientProfileFailureShouldDisposeTransportAndPreserveBothFailures` covers direct transport profile rollback.
2. [x] `DirectClientConstructionFailureShouldDisposeTransportAndPreserveBothFailures` covers direct transport constructor rollback.
3. [x] `DynamicResolverValidationFailureShouldDisposeResolverAndPreserveBothFailures` covers resolver validation rollback.
4. [x] `ServerValidationFailureShouldPreserveRuntimeContextCleanupFailure` and `ServerConstructorFailureShouldDisposeRuntimeContextAndPreserveBothFailures` cover both Server rollback boundaries.
5. [x] Complete non-incremental build, full tests, performance/package gates, documentation, and local commit.
