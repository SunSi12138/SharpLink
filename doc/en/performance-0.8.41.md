# SharpLink 0.8.41 performance validation

On Apple M4 with .NET SDK 10.0.102, the exact detached `dd431f5` checkout and final candidate ran an interleaved real TCP unary harness. Each process warmed up 2,000 calls, then measured 9 x 20,000-call sample medians for plain RPC and one pass-through Client plus Server interceptor. The final result is the median of five independent processes per side.

| Build | Plain-RPC five process medians | Process median | Allocation |
|---|---|---:|---:|
| Exact 0.8.40 baseline | 38.498 / 39.792 / 38.270 / 39.129 / 38.694 us | 38.694 us | 320.01-320.04 B/op |
| 0.8.41 candidate | 37.285 / 37.652 / 38.832 / 39.312 / 39.200 us | 38.832 us | 320.01-320.02 B/op |

| Build | Intercepted-RPC five process medians | Process median | Allocation |
|---|---|---:|---:|
| Exact 0.8.40 baseline | 39.748 / 39.911 / 40.995 / 40.659 / 39.725 us | 39.911 us | 1,560.01-1,560.03 B/op |
| 0.8.41 candidate | 40.302 / 41.148 / 40.730 / 38.494 / 40.211 us | 40.302 us | 1,560.01-1,560.03 B/op |

Plain RPC changed by +0.36% and intercepted RPC by +0.98%; per-process ranges overlap substantially and allocation is unchanged, so there is no measurable regression. A dedicated shared-dispatcher harness directly covered the new reference-item nullability branch with 15 x 2,000,000 dispatch/consume operations per process. Exact baseline processes measured 13.860 / 13.527 / 14.121 ns/op and candidate processes measured 13.860 / 13.871 / 13.643 ns/op: both process medians are exactly 13.860 ns/op, with 1.333 B/op on both sides.

The exact checkout, harnesses, and raw process output are retained under `artifacts/performance/0.8.41-baseline/`, `artifacts/performance/0.8.41-unary-ab/`, and `artifacts/performance/0.8.41-stream-ab/`. The combined gate passed non-incremental Release with no warnings/errors, Generator 120/120, Unit 490/490, Integration 250/250, 120-second shared-memory Chaos, NativeAOT TCP, seven-package pack, and fresh-cache TCP/shared-memory functional smoke.
