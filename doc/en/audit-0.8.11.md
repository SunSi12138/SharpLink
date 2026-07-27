# SharpLink 0.8.11 Deep Audit

Chinese: [`../audit-0.8.11.md`](../audit-0.8.11.md)

Using 0.8.10 commit `e84c851` as baseline, five P2-or-higher defects received pre-fix failing proofs. Client and Server runtime registration and replacement lost their primary structured rejection when generated Adapter Scope rollback also failed. Server profile binding ran outside the cleanup boundary and leaked its newly built Runtime Context. Exactly five new regressions failed out of Unit 394 while the previous 389 tests passed; Unit 394/394 and the complete release gates passed after the fixes.

Server candidate-service rollback also attempts every owner after an earlier disposal failure. Ordinary structured rejections retain their result contract; only a simultaneous rollback failure now produces `AggregateException`, ordered with the transaction failure first.

See [`migration-0.8.11.md`](migration-0.8.11.md) and [`performance-0.8.11.md`](performance-0.8.11.md).
