# SharpLink 0.8.5 Performance Validation

Chinese: [`../performance-0.8.5.md`](../performance-0.8.5.md)

On Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8, the 0.8.4 `a7f8a24` baseline and 0.8.5 candidate used one launch, five warmups, and fifteen measurement iterations. Published-client accessor lookup measured 1.457 to 1.483 ns and remained at 0 B/op.

The candidate and baseline 99.9% confidence intervals were 1.443–1.522 ns and 1.404–1.510 ns respectively and overlap substantially, so this is treated as no-regression evidence rather than a claimed difference. The low-frequency publication/stop path uses a gate to close the race, while steady-state published-client reads remain lock-free and allocation-free. The remaining changes affect exception and rollback paths only. Raw reports are retained under `artifacts/performance/0.8.5-accessor-ab/`.
