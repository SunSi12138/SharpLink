# SharpLink 0.8.8 Performance Validation

Chinese: [`../performance-0.8.8.md`](../performance-0.8.8.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8 used one launch, five warmups, and fifteen measurements. Normal anonymous-pipe offer allocation/disposal measured 2.590 µs (99.9% CI 2.580–2.600) on 0.8.7 and 2.592 µs (2.583–2.601) on 0.8.8, with 2.13 KB on both. The intervals overlap and allocation is unchanged; the other changes execute only on exceptional cleanup paths. One initial launch rejected during benchmark generation because of duplicate project names produced no measurements and is excluded. Valid raw reports are under `artifacts/performance/0.8.8-transport-dispose-ab/`.
