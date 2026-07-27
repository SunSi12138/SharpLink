# SharpLink 0.8.43 performance validation

On Apple M4 with .NET SDK 10.0.102, exact 0.8.42 commit `cd2de157` and the candidate ran as independent Release processes. Three adjacent Balanced TCP pairs used one connection, concurrency 8, stream size 256, one-second warmup, and three-second measurement per stage, reversing order in the middle pair. Every stage completed with zero failures.

| workload | 0.8.42 median QPS | candidate median QPS | paired median | median P50 | median P99 |
|---|---:|---:|---:|---:|---:|
| unary control | 163,931 | 163,672 | -0.6% | 49 → 49 us | 69 → 70 us |
| c2s | 7,910 | 8,028 | +1.5% | 1,004 → 981 us | 1,387 → 1,354 us |
| s2c | 8,568 | 8,584 | -1.8% | 943 → 942 us | 1,084 → 1,103 us |
| duplex | 4,824 | 5,019 | +4.0% | 1,626 → 1,580 us | 2,560 → 2,199 us |

The isolated 0.7.11/0.8.41 investigation first measured paired-median Balanced c2s/s2c/duplex changes of -4.3%/-7.3%/-14.8%, then bisected the first consistent loss to 0.8.0. `RecordConsumed` was followed by a second acquisition of the same gate for an almost-always-empty cross-stream credit queue, approximately 512 redundant locks for a size-256 duplex RPC. Removing only that empty drain changed three paired samples by +6.7%, +9.6%, and +3.7% (median +6.7%), with -6.2% P50/P99 and -8.8% CPU per stream. The 0.8.43 nullable-queue fast path preserves cross-stream credit correctness without the empty lock.

A dedicated MemoryDiagnoser corrected an earlier process-wide B/item artifact: size 32 changed from 6.57 to 6.58 KB and size 256 from 31.09 to 31.29 KB. The apparent +20.7% LoadTest value came from dividing background process allocation by fewer completed items and is not treated as a product allocation regression. The new fast path adds no per-item allocation.

Raw exact-0.8.42/candidate JSON and logs are under `artifacts/performance/0.8.43-stream-ab/`; the isolated performance checkout retains the 0.7.11 comparison, bisection, causal, and MemoryDiagnoser evidence. The combined gate passed non-incremental Release with no warnings/errors, Generator 121/121, Unit 496/496, Integration 252/252, 120-second shared-memory Chaos, and NativeAOT TCP. Seven-package and fresh-cache package smoke validate the final versioned packages.
