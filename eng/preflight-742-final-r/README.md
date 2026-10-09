# Frozen R full validation

This workflow measures dev commit 0fe26024b114bb6e78411a9b86276086c045d03d against frozen production tree df4383c7d00ee4a31c129ae42958e013f4ff5385. There are no new production optimizations or candidate test overlays. R is reconstructed from recipe 06b737df0d9aab66caed9c446a572b3a8ce4efae and two hash-pinned project-source patches. Exact indexed trees and tracked source bytes are verified. Dev receives only the two original common timing harness files.

The original full workflow is from helper 211c7a94f7107158cd69cfb251ca33156487c48e, run 37814152170. Timing commands, scenarios, repetitions, AB/BA/AB ordering, durations, windows, operation caps and validators are unchanged:

- JIT candidate-internal keyed/resolved micro: PGO off/on, three process repeats each, all 232 rows per process; sequential repetitions 3 and contention repetitions 6.
- Actual NativeAOT candidate-internal micro: the same 232 rows and repetition counts.
- JIT RPC: seven scenarios, TCP/SharedMemory, dev/R, three repeats, totaling 84 processes; 30 warmup operations, 5 seconds and 200000-operation cap.
- JIT c8: TCP/SharedMemory, stream sizes 1/10000, dev/R, three repeats, totaling 24 processes and 96 rows including unary controls; 8192/65536 byte windows, one-second warmup, two-second measurement, recording off.
- Actual NativeAOT RPC: the same original 84-process population; unchanged JSON-only host adaptation outside the measured loop.
- Actual NativeAOT ready-writer: all 171 original transport checks, using the original isolated test hook after performance timing.

Source and measured binary hashes are retained before and after timing. Four-CPU affinity, .NET SDK 10.0.112 and runtime 10.0.12 are required. Native executables, symbols, logs and raw reports are retained. Native executable archives are uploaded separately to keep each download bounded. No retry-to-green, omitted cells, altered thresholds or substitution of G2 results is permitted.

Correctness and codegen for R are separately established by run 37855801421 (2142 tests in each of three configurations). That evidence does not replace these missing runtime populations or the final promoted PR head's CI. Existing negative observations remain negative. Collection success is not a performance acceptance verdict, PR Ready decision, merge or deployment authorization.
