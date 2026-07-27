# SharpLink 0.8.7 Performance Validation

Chinese: [`../performance-0.8.7.md`](../performance-0.8.7.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8 used one launch, five warmups, and fifteen measurements. Normal ClientConnection disposal measured 1.145 µs (99.9% CI 1.143–1.148) on 0.8.6 and 1.146 µs (1.142–1.151) on 0.8.7, with 18.51 KB on both. Two earlier Task/field-allocating designs were rejected at 18.56/18.58 KB; the final implementation reuses RpcSession teardown. Raw reports are under `artifacts/performance/0.8.7-client-dispose-ab/`.
