# SharpLink 0.8.9 Performance Validation

Chinese: [`../performance-0.8.9.md`](../performance-0.8.9.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8 used one launch, five warmups, and fifteen measurements. Normal anonymous-pipe offer allocation/disposal measured 2.576 µs (99.9% CI 2.563–2.589) on 0.8.8 and 2.597 µs (2.583–2.610) on 0.8.9, with 2.13 KB on both and overlapping intervals. The first design measured 2.608 µs / 2.19 KB and was rejected for unconditional `Task`/lock allocation; the final design caches a `Task` only for genuinely asynchronous or failed disposal. Raw reports are under `artifacts/performance/0.8.9-listener-dispose-ab/`.
