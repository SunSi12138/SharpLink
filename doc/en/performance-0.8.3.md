# SharpLink 0.8.3 Performance Validation

Chinese: [`../performance-0.8.3.md`](../performance-0.8.3.md)

On Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8, both 0.8.2 baseline and 0.8.3 candidate used three launches, three warmups, and ten measurement iterations. Two-entry construction moved from 10.47 to 10.13 ns and stayed at 80 B/op. Two-entry wire decode moved from 68.33 ns / 280 B to 61.89 ns / 224 B. The removed 56 B matches the redundant two-entry array.
