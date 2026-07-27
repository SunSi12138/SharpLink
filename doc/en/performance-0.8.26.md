# SharpLink 0.8.26 performance validation

Chinese: [`../performance-0.8.26.md`](../performance-0.8.26.md)

On Apple M4 / .NET SDK 10.0.102, independent Release processes ran the same Roslyn harness against 0.8.25 commit `0773496` and the final candidate. The input contains 40 namespace-isolated RPC contracts and 400 ordinary instance methods. Results use five warmup iterations and 101 measured samples, with GC convergence before every sample.

The baseline median was 14.755 ms (P25 12.534 / P75 22.503). The first candidate measured 16.241 ms, about a 10% regression, and was rejected. After combining the Oneway/Timeout attribute traversal and removing a captured lambda from generated-local naming, the final candidate measured 13.530 ms (P25 12.703 / P75 18.953) and passed the latency gate.

Median allocation on the same compiler thread moved from 28,495,488 to 28,572,128 bytes, an increase of 76,640 bytes (0.27%). The added cost is confined to Generator analysis; valid runtime request paths are unchanged. A separate 16-key dictionary construction/insertion comparison measured the original path at 171.891 ns and the null-guard path at 170.941 ns, showing no runtime regression. The harness remains at `artifacts/performance/0.8.25-generator-harness/`, and the 0.8.26 baseline worktree is under `artifacts/performance/0.8.26-baseline/`.
