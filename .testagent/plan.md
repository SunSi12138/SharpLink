# 0.8.23 regression-test plan

1. [x] Prove Boolean blit collections reject non-canonical elements across all five shapes.
2. [x] Prove Rune and decimal blit collections reject invalid elements.
3. [x] Prove temporal blit collections reject invalid elements.
4. [x] Prove DateTimeOffset collections validate values and clear native padding.
5. [x] Prove truncated shared-memory responses map to structured Unavailable.
6. [x] Run complete pre-fix Unit and Integration suites and record the exact failure set.
7. [x] Implement only proven fixes, review assertions/pseudo-mutations, and run performance A/B.
8. [x] Complete release gates and documentation; prepare the local 0.8.23 commit.
