# 0.8.17 regression-test plan

1. [x] Prove concurrent multi-cluster unregister callers share the original rejected child operation.
2. [x] Prove Client and Server TLS snapshots preserve isolated chain-policy and RSA-padding settings.
3. [x] Prove inconsistent request and unknown response capability sets are rejected.
4. [x] Prove partition admission configuration is frozen before runtime use.
5. [x] Prove state-store and writer-pool aggregate sizing has hard validation bounds.
6. [x] Run the complete pre-fix Unit probe and record the exact failure set.
7. [x] Implement only proven fixes, review assertions/pseudo-mutations, and run performance A/B.
8. [x] Complete release gates and documentation; prepare the local 0.8.17 commit.
