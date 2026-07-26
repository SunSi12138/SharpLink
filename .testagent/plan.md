# 0.8.4 regression-test plan

1. [x] Deterministically block generated and fallback codec creation across publication; prove a post-publication lookup cannot observe the superseded codec.
2. [x] Block codec resolution across context disposal; prove the in-flight call fails and the disposed cache cannot be repopulated.
3. [x] Replay a retained pre-admission frame into an asynchronously gated dispatcher; prove registration never blocks and later frames preserve order.
4. [x] Attach a dispatcher whose configuration callback reenters the same request registry; prove callbacks execute outside the registry lock.
5. [x] Reproduce and fix multi-cluster route divergence after child replacement publication plus old-generation cleanup failure.
6. [x] Run focused regressions, complete suites, Release build, performance comparison, diff checks, documentation, and local commit gates.
