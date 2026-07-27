# SharpLink 0.8.25 performance validation

Chinese: [`../performance-0.8.25.md`](../performance-0.8.25.md)

On Apple M4 / .NET SDK 10.0.102, independent Release processes ran the same Roslyn harness against 0.8.24 commit `09d6078` and the final candidate. The input contains 40 namespace-isolated RPC contracts and 400 ordinary instance methods. Results use five warmup iterations and 101 measured samples, with GC convergence before every sample.

The baseline median was 15.953 ms (P25 12.323 / P75 23.967), and the candidate measured 13.577 ms (P25 12.481 / P75 21.864). Quartiles overlap and show no latency regression. The abstract-member check returns without allocation for ordinary method-only contracts. Its HashSet is created lazily only when a property or event is found, and hint suffixes reuse the existing contract ID instead of computing another SHA.

Median allocation on the same compiler thread moved from 28,454,352 to 28,495,328 bytes, an increase of 40,976 bytes (0.14%). The cost belongs to new source-diagnostic and identity metadata. Runtime assemblies, valid request Codecs, and dispatch hot paths are unchanged. The harness remains at `artifacts/performance/0.8.25-generator-harness/`, with the baseline worktree in the same task artifact directory.
