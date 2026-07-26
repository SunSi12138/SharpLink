# SharpLink 0.8.6 Performance Validation

Chinese: [`../performance-0.8.6.md`](../performance-0.8.6.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8 used one launch, five warmups, and fifteen measurements. Normal RpcSession disposal measured 950.9 ns (99.9% CI 944.7–957.1) on 0.8.5 and 955.8 ns (949.9–961.6) on 0.8.6; intervals overlap and allocation remained 17.5 KB. Other changes are exceptional/terminal cold paths. Raw reports are under `artifacts/performance/0.8.6-dispose-ab/`.
