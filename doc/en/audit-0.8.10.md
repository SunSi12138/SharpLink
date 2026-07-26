# SharpLink 0.8.10 Deep Audit

Chinese: [`../audit-0.8.10.md`](../audit-0.8.10.md)

Using 0.8.9 commit `afe9f3a` as baseline, five P2-or-higher defects received pre-fix failing proofs: fixed-endpoint build, profile binding, single-Manifest preparation, multi-Manifest Context construction, and outer Client Context rollback lost either the primary or cleanup failure. Exactly five new regressions failed out of Unit 389; Unit 389/389 and the complete release gates passed after the fixes.

See [`migration-0.8.10.md`](migration-0.8.10.md) and [`performance-0.8.10.md`](performance-0.8.10.md).
