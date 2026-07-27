# SharpLink 0.8.24 performance validation

Chinese: [`../performance-0.8.24.md`](../performance-0.8.24.md)

On Apple M4 / .NET SDK 10.0.102, independent Release processes ran the same Roslyn harness against 0.8.23 commit `3202bd7` and the final candidate. The input contains 20 RPC contracts, 400 methods with valid `[Timeout]` attributes, and 200 valid union cases. Final results use five warmup iterations and 101 measured samples, with GC convergence before every sample.

The first implementation added a separate timeout `SyntaxProvider` analysis pipeline. Its 15-sample median moved from 57.290 ms to 69.062 ms, about a 20.5% regression, so that design was rejected. The final implementation folds `SHARPLINK050` into the existing invalid-method traversal and uses a symbol fast path for common direct union relationships. The 101-sample comparison measured 41.029 ms (P25 38.638 / P75 53.928) to 40.675 ms (P25 38.796 / P75 50.502), with no latency regression.

Median allocation on the same compiler thread moved from 27,019,920 to 27,175,096 bytes. Across 400 timeout and 200 union validations per run, the increase is 155,176 bytes, or 0.57%. This is compile-time diagnostic metadata cost; runtime sources and valid RPC serialization/dispatch hot paths are unchanged. The harness remains under `artifacts/performance/0.8.24-generator-harness/`, with the baseline worktree in the same task artifact directory.
