# SharpLink 0.8.7 Deep Audit

Chinese: [`../audit-0.8.7.md`](../audit-0.8.7.md)

Using 0.8.6 commit `3d5da89` as baseline, five P2-or-higher defects received pre-fix failing proofs: early concurrent ClientConnection disposal return, lost multi-scope Runtime Context failures, non-convergent Hosted Server stop, lost secondary connection-close failure, and cancellation callbacks stranding pending calls/streams. Unit 374/374 and the complete release gates passed after fixes.

See [`migration-0.8.7.md`](migration-0.8.7.md) and [`performance-0.8.7.md`](performance-0.8.7.md).
