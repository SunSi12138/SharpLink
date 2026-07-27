# SharpLink 0.8.12 Deep Audit

Chinese: [`../audit-0.8.12.md`](../audit-0.8.12.md)

Using 0.8.11 commit `a10081f` as baseline, five P2-or-higher defects received pre-fix fault-injection proofs. Direct Client profile binding and later construction failures did not release the Client-owned transport; dynamic endpoint validation did not release the resolver; Server service validation could be replaced by Runtime Context cleanup failure; and logger construction escaped the old Server cleanup boundary completely. The focused pre-fix run failed exactly five tests, while the committed 0.8.11 baseline had Unit 394/394. Unit 399/399 and the complete release gates pass after the fixes.

The final Client design aggregates transport/resolver and Runtime Context rollback only in the existing outer exception cold path. Server resources transfer only after successful construction; failure disposes every created registration and internal owner while retaining the ordered failure set.

See [`migration-0.8.12.md`](migration-0.8.12.md) and [`performance-0.8.12.md`](performance-0.8.12.md).
