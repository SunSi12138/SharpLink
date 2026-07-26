# 0.8.18 regression-test plan

1. [x] Prove cancelled Hosted Client Stop still disposes every transferred Client owner.
2. [x] Prove a timer-range-exceeding dynamic-module drain timeout remains pending and completes after the lease drains.
3. [x] Prove a huge configured send-flush latency does not overflow into an immediate flush or pump fault.
4. [x] Prove Server call concurrency rejects a deadline-scan snapshot beyond the hard bound.
5. [x] Prove a throwing stream dispatcher cannot strand sibling streams or transport disposal.
6. [x] Run the complete pre-fix Unit probe and record the exact failure set.
7. [x] Implement only proven fixes, review assertions/pseudo-mutations, and run performance A/B.
8. [x] Complete release gates and documentation; prepare the local 0.8.18 commit.
