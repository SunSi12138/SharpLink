# SharpLink 0.8.9 Deep Audit

Chinese: [`../audit-0.8.9.md`](../audit-0.8.9.md)

Using 0.8.8 commit `c90525b` as baseline, five P2-or-higher defects received pre-fix failing proofs: skipped reader convergence after shared-memory control cleanup failure, early concurrent Stop return in both Hosted Client variants, early concurrent asynchronous listener disposal return, and skipped later anonymous-pipe connections after one cleanup failure. Exactly five new regressions failed out of Unit 384; Unit 384/384 and the complete release gates passed after the fixes.

See [`migration-0.8.9.md`](migration-0.8.9.md) and [`performance-0.8.9.md`](performance-0.8.9.md).
