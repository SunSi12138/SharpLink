# 0.8.20 regression-test plan

1. [x] Prove over-range RPC/TLS/shared-memory handshake timeouts fail during configuration.
2. [x] Prove a far-future WaitForReady deadline remains cancellable.
3. [x] Prove a far-future pending-slot deadline remains cancellable.
4. [x] Prove a timer-range-exceeding Server graceful wait remains pending until its owner completes.
5. [x] Prove generated DTO strings reject invalid UTF-8 instead of replacing bytes.
6. [x] Run the complete pre-fix Unit probe and record the exact failure set.
7. [x] Implement only proven fixes, review assertions/pseudo-mutations, and run performance A/B.
8. [x] Complete release gates and documentation; prepare the local 0.8.20 commit.
