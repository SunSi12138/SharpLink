# 0.8.21 regression-test plan

1. [x] Prove malformed shared-memory mapping-path UTF-8 is rejected before path validation.
2. [x] Prove null generated collections reject trailing bytes.
3. [x] Prove generated DTO string serialization rejects isolated UTF-16 surrogates.
4. [x] Prove metadata snapshots reject invalid Unicode before Protocol v2 encoding.
5. [x] Prove per-call scope creation failure releases its dynamic module lease.
6. [x] Run complete pre-fix Unit and Integration probes and record the exact failure set.
7. [x] Implement only proven fixes, review assertions/pseudo-mutations, and run performance A/B.
8. [x] Complete release gates and documentation; prepare the local 0.8.21 commit.
