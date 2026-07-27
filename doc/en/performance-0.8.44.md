# SharpLink 0.8.44 performance validation

On Apple M4 with .NET SDK 10.0.102, exact 0.8.43 commit `9789fbe` and the candidate ran as independent Release processes. Three interleaved Balanced TCP full-stream pairs used one connection, concurrency 8, and stream size 256; every unary/c2s/s2c/duplex stage completed without failure.

The short c2s samples moved in one direction while normalized allocation and CPU moved sharply in the opposite direction, so they were insufficient to classify a regression. Five additional adjacent, order-reversing c2s pairs used two-second warmup and ten-second measurement:

| metric | five-pair median change | conclusion |
|---|---:|---|
| QPS | -0.05% | no measurable throughput regression |
| P50 | -0.19% | stable |
| P99 | +0.27% | stable |
| CPU/operation | -0.38% | stable |

The five QPS changes were -1.06%, +1.07%, +0.47%, -0.05%, and -0.07%, crossing zero with a near-zero median. Process-wide allocated bytes divided by completions remains sensitive to startup, background work, and the throughput denominator, so it is only supporting evidence and no allocation improvement is claimed.

This release changes shutdown and terminal-failure cleanup. The ordinary unary, stream-item codec, and send-pump enqueue paths allocate no new objects; terminal stream closure adds only structured `finally` cleanup. Raw JSON and logs are under `artifacts/performance/0.8.44-stream-ab/` and `artifacts/performance/0.8.44-c2s-long-ab/`.

The combined gate passed a non-incremental Release build with no warnings/errors, Generator 121/121, Unit 503/503, Integration 252/252, 120-second shared-memory Chaos, independent-process SharedMemory NativeAOT, seven-package pack, and fresh-cache PackageSmoke.
