# 0.8.22 regression-test plan

1. [x] Prove generated Boolean DTO members reject non-canonical bytes.
2. [x] Prove generated Rune DTO members reject invalid Unicode scalars.
3. [x] Prove generated decimal DTO members reject invalid layouts.
4. [x] Prove generated DateOnly, DateTime, and TimeOnly DTO members reject invalid values.
5. [x] Prove generated DateTimeOffset DTO members use the canonical validated Codec payload.
6. [x] Run the complete pre-fix Integration and Generator suites and record the exact failure set.
7. [x] Implement only proven fixes, review assertions/pseudo-mutations, and run performance A/B.
8. [x] Complete release gates and documentation; prepare the local 0.8.22 commit.
