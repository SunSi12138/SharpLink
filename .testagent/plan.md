# 0.8.3 regression-test plan

1. [x] Prove endpoint snapshots freeze nested attributes, not only the outer list.
2. [x] Prove asynchronous Stop returns before a blocking shutdown callback completes.
3. [x] Preserve primary and cleanup failures for fixed and endpoint-cluster connection attempts.
4. [x] Preserve primary and cleanup failures across Client, multi-cluster, and Server Hosted startup.
5. [x] Benchmark metadata construction/decode and remove only the empirically redundant decode copy.
6. [x] Complete versioned Release, Generator, Unit, Integration, diff, and review gates.
