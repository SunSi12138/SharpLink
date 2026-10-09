# Same-host dev / pre-refactor / R comparison

This historical comparison measures the same five workloads as run 37859141478, replacing its G2 control with the last ready-batch candidate measured before the lifecycle redesign. It introduces no production optimization or test overlay.

- D: dev commit 0fe26024b114bb6e78411a9b86276086c045d03d.
- P: production tree ea21543ca4d267938834ac24bead069aac2327c0, measured in run 37741200571.
- R: production tree df4383c7d00ee4a31c129ae42958e013f4ff5385, measured in run 37859141478.

P and R are reconstructed from recipe commit 06b737df0d9aab66caed9c446a572b3a8ce4efae using hash-pinned original patches. Their complete staged trees and unmodified working trees must match the identities above. D retains its original index, with only the two common benchmark harness files overlaid identically from P. All three roots must use byte-identical harness files. Full tracked source and built application/dependency hashes are recorded before/after timing.

All six permutations of D/P/R run on one GitHub Ubuntu 24.04 job with the same four-CPU affinity, .NET SDK 10.0.112, runtime 10.0.12, ReadyToRun 0, tiering 1, TieredPGO 1, QuickJitForLoops 1. Original commands are unchanged:

1. TCP Client100x16 RPC.
2. SharedMemory Client100x4096 RPC.
3. SharedMemory Server1x16 RPC.
4. SharedMemory c8 size 1: unary, c2s, s2c, duplex in the original order.
5. SharedMemory c8 size 10000: the same four-operation sequence.

RPC uses 30 warmup operations, 5 measurement seconds and 200000-operation cap. C8 uses concurrency 8, 8192/65536 byte receive windows, warmup 1 second, duration 2 seconds and recording off. There are 90 launches and 198 result rows. Every launch gets one attempt; failures and every original report are retained. No alternate window, tracing, per-item instrumentation or selective retry is permitted. Source and binary drift is fatal.

Report P/D, R/D and R/P ratios of medians and all six paired differences for throughput, CPU/op and allocation/op; also report D-normalized medians with D = 1. Allocation/item divides by stream size except unary. C8 counters include drain whereas QPS uses the measurement interval; do not claim formal latency. A 1–2% single-element short-stream throughput tolerance does not waive other cells or metrics. Long and short outcomes are not averaged together.

This bounded JIT comparison does not provide TCP c8, NativeAOT runtime performance or complete original acceptance coverage. Prior measurements remain evidence. Successful collection alone is not acceptance.
