# SharpLink 0.8.42 performance validation

On Apple M4 with .NET SDK 10.0.102, exact 0.8.41 commit `d0e0df4` and the final candidate ran in independent processes.

Balanced single-connection TCP unary at concurrency 8 used a two-second warmup and five-second measurement in ten processes per side, reversing order for the second five pairs. Median QPS was 166,576 versus 165,315 (-0.76%); P50 remained 48 us, P99 remained approximately 72 us, and allocation changed from 172.08 to 171.98 B/call.

The nullable Codec gate ran 15 x 3,000,000 contiguous `int?` decodes in five alternating processes. Present-value process medians were 5.155 versus 5.090 ns/op. Canonical null was 5.444 versus 5.937 ns/op, the unavoidable 0.493 ns fixed-body validation cost; both paths allocate zero. A uniform decorator prototype measured about 10.3/21.5 ns and was rejected in favor of inlining validation into existing Codec branches.

Exact 0.8.41 exited 134 in both independent TCP `operation=all` repetitions, with 3/5 s2c and 5/5 c2s focused-process crashes. The fix completed 16/16 processes and all 64 unary/c2s/s2c/duplex stages with zero failures. Raw A/B, microbenchmark, and load output is retained under `artifacts/performance/0.8.42-*` and `artifacts/0.8.42-sendpump-repro/`.

The combined gate passed non-incremental Release with no warnings/errors, Generator 121/121, Unit 493/493, Integration 250/250, 120-second shared-memory Chaos, NativeAOT TCP, seven-package pack, and fresh-cache TCP/shared-memory functional smoke.
