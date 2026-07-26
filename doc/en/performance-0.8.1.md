# SharpLink 0.8.1 Performance Validation

Chinese: [`../performance-0.8.1.md`](../performance-0.8.1.md)

Three strictly alternating BenchmarkDotNet rounds compared 0.8.0 commit `7a99fc6` with direct-to-List decoding on Apple M4 / .NET 10.0.2. At 16 integers, median latency was 87.167 μs → 87.553 μs (99.56% throughput) and allocation was 560 → 472 B/op. At 256 integers, latency was 91.167 μs → 88.915 μs (102.53% throughput) and allocation was 2480 → 1432 B/op. Both points pass the 97% throughput gate while eliminating the intermediate array.

Two setup failures with no measurements were excluded: one compiler process exited with code 139, and one run found duplicate benchmark projects before isolated A/B worktrees were used. Valid raw reports remain in task-local artifacts.

## Runtime sentinel

The seven runtime hot-path benchmarks outside this batch's changed paths also completed, with every B/op count identical to 0.8.0. Their absolute latencies all shifted upward by roughly the same 1.5x host-wide factor, including unrelated pending-table, frame-parser, and call-context cases, so that non-alternating absolute run was not used as a regression decision. The only steady-state data path changed in 0.8.1, `BlitListCodec<T>`, is gated by the stricter three-round alternating A/B comparison above.
