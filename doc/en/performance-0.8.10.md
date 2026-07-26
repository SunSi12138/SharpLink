# SharpLink 0.8.10 Performance Validation

Chinese: [`../performance-0.8.10.md`](../performance-0.8.10.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8 used one launch, five warmups, and fifteen measurements. The alternating final normal Runtime Context build/disposal runs measured 346.1 ns (99.9% CI 345.4–346.7) on 0.8.9 and 343.7 ns (342.7–344.8) on 0.8.10, with 3.9 KB on both. An early candidate before cold-path extraction measured 350.2 ns; the final no-inline rollback path shows no regression. Raw reports are under `artifacts/performance/0.8.10-context-build-ab/`.
