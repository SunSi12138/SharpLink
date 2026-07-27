# SharpLink 0.8.30 performance validation

Chinese: [`../performance-0.8.30.md`](../performance-0.8.30.md)

On Apple M4 / .NET SDK 10.0.102, independent alternating Release processes compared 0.8.29 commit `88039d5` with the final candidate.

The Roslyn harness contains 40 contracts and 400 methods, with five warmups and 101 samples per process. An initial design that added `ReturnsValueTask` to incremental record equality regressed latency by about 8% and was rejected. The final non-positional computed property measured 15.438 to 15.411 ms (-0.2%) using the median of three process medians; same-run compiler-thread allocation was 28,570,544 to 28,570,408 bytes.

The local health harness warmed 20,000 calls and collected 15 samples of two million calls while cycling all three statuses. Allocation fell from 96 to 0 B/call. Apple Silicon scheduling produced a roughly 2/12 ns candidate bimodal distribution versus a roughly 7/13 ns baseline range; the worst process median was about 5 ns higher, with overlapping quartiles. This explicitly accepts zero recurring GC pressure in a once-per-external-poll path without claiming a pure latency win. RPC, codec, and transport-I/O hot paths are unchanged.

Harnesses and the baseline worktree remain under `artifacts/performance/0.8.30-generator-ab/`, `artifacts/performance/0.8.30-health-ab/`, and `artifacts/performance/0.8.30-baseline/`.
