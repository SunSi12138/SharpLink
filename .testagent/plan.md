# 0.8.29 regression-test plan

1. [x] Prove pending-table post-dispose stream rejection and Dispose/Rent linearization.
2. [x] Prove a future wall-clock activity value cannot suppress client heartbeat timeout.
3. [x] Prove every named-pipe/shared-memory entry point rejects invalid logical names during configuration.
4. [x] Prove abstract Unix-domain endpoint snapshots retain their exact serialized address.
5. [x] Prove ready multi-cluster state reads allocate no managed memory.
6. [x] Run the complete pre-fix Unit suite and record all existing passes plus the exact new failure set.
7. [x] Implement only proven fixes, then perform assertion-quality and pseudo-mutation reviews.
8. [x] Run exact-final-tree non-incremental build, focused/full tests, package smoke, documentation, and performance gates; create the local 0.8.29 commit.
