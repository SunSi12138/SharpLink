# 0.8.16 regression-test plan

1. [x] Prove a deadline beyond the native timer range can be registered without a synchronous failure.
2. [x] Prove Runtime Context disposal drains idle buffers and governs active leases returned afterward.
3. [x] Prove Server Stop surfaces an immediate listener cleanup failure.
4. [x] Prove a successful Hosted Server does not retain its startup cancellation token.
5. [x] Prove oversized pending-request capacities fail validation before allocating a connection table.
6. [x] Run the complete pre-fix Unit probe and record the exact failure set.
7. [x] Implement only proven fixes, review assertions/pseudo-mutations, and run performance A/B.
8. [x] Complete release gates, documentation, and the local 0.8.16 commit.
