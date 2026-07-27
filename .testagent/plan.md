# 0.8.31 regression-test plan

1. [x] Prove custom mutable `EndPoint` instances are not snapshotted.
2. [x] Prove Unix socket cleanup deletes a later caller-owned path replacement.
3. [x] Prove a public raw frame token from another writer silently corrupts its target, then keep the duplicate raw writer internal without slowing the generated packet path.
4. [x] Prove anonymous-pipe offers expose inherited handles and lack transfer completion.
5. [x] Prove obsolete/internal-only implementation types remain exported.
6. [x] Run the complete pre-fix Unit suite and record all existing passes plus exactly five new failures (473 total: 468 existing pass, 5 new fail).
7. [x] Implement only proven fixes, then perform assertion-quality and pseudo-mutation reviews.
8. [x] Run exact-final-tree build, full tests, packages, fresh-cache smoke, documentation, and performance gates; create the local 0.8.31 commit.
