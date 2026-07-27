# SharpLink 0.8.27 performance validation

Chinese: [`../performance-0.8.27.md`](../performance-0.8.27.md)

On Apple M4 / .NET SDK 10.0.102, independent Release processes ran the same runtime harness against 0.8.26 commit `1d8325e` and the final candidate. Each case used 20,000 warmup operations and 15 samples. Writer rent/return used 5,000,000 operations per sample, pending Int32 response used 1,000,000, and stream dispatch/consume used 2,000,000.

Writer-pool rent/return measured 8.884 to 8.830 ns (P25/P75 8.583/9.873 to 8.541/9.500), with 0 B/op for both. The post-enqueue ownership check caused no measurable regression.

Pending Int32 response completion measured 44.174 to 43.836 ns (P25/P75 42.531/45.075 to 42.532/45.977), with 24 B/op for both. Stream dispatch/consume measured 16.795 to 16.803 ns (P25/P75 15.059/17.315 to 16.353/17.613), with the same amortized 1.333 B/op. The intervals overlap and show no measurable cost from the secondary-token branch. The harness remains under `artifacts/performance/0.8.27-runtime-ab/`, with the baseline worktree at `artifacts/performance/0.8.27-baseline/`.
