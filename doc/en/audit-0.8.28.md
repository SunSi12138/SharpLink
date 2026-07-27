# SharpLink 0.8.28 deep audit

Chinese: [`../audit-0.8.28.md`](../audit-0.8.28.md)

Using 0.8.27 commit `656271b` as the baseline, this batch proves and fixes five P2 issues: native integer-second overflow in TCP keep-alive configuration; late timer-range failures in all three admission rate policies; undefined named-pipe values and the server-only `FirstPipeInstance` client flag accepted until connect/accept; zero-tick sliding-window segments; and binary error writers emitting undefined codes that their matching reader rejects.

The complete pre-fix Unit run contained 459 tests: all 454 existing tests passed and exactly the five new probes failed. Strengthened assertions cover inclusive valid maxima, one-tick overflow, all server pipe flags plus the client/server flag distinction, the one-tick segment boundary, and writer state after an invalid error code.

Three hypotheses were rejected rather than forced into the release: maximum DNS/retry jitter saturates on the supported .NET 10/arm64 runtime, and near-`int.MaxValue` excess flow-control credit already becomes `ProtocolViolation`. The pending-table Dispose/Rent race remains uncounted without a deterministic witness.

The final non-incremental Release build completed with zero warnings/errors; Generator 101/101, Unit 459/459, Integration 237/237, seven packages, and fresh-cache package smoke passed. Alternating runtime A/B preserved allocations and passed the 5% gate. See [`../performance-0.8.28.md`](../performance-0.8.28.md) and [`../migration-0.8.28.md`](../migration-0.8.28.md).
