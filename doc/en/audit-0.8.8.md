# SharpLink 0.8.8 Deep Audit

Chinese: [`../audit-0.8.8.md`](../audit-0.8.8.md)

Using 0.8.7 commit `30da5f7` as baseline, five P2-or-higher defects received pre-fix failing proofs: an anonymous-pipe input-handle leak, a shared-memory mapping leak after control cleanup failure, and lost later failures at the single-module, multi-module, and server-wide cleanup boundaries. Exactly five new regressions failed while the previous 374 unit tests passed; Unit 379/379 and the complete release gates passed after the fixes.

See [`migration-0.8.8.md`](migration-0.8.8.md) and [`performance-0.8.8.md`](performance-0.8.8.md).
