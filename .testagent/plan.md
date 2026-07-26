# 0.8.9 regression-test plan

1. [x] Prove shared-memory control cleanup still joins its reader after stream disposal fails.
2. [x] Prove single-client and multi-cluster Hosted Stop callers join one terminal operation.
3. [x] Prove public asynchronous listeners share disposal completion and do not skip queued owners after one failure.
4. [x] Complete full validation, performance and package gates, documentation, and local commit after five verified findings.
