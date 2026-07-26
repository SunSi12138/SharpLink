# SharpLink 0.7.11 Performance Validation

Chinese: [`../performance-0.7.11.md`](../performance-0.7.11.md)

Baseline: `2dd4e84870b2694640ecd4ba61bec51f461e7226`, SharpLink 0.7.10, MemoryPack 1.21.4. Candidate: 0.7.11, SharpPack 1.0.1, manifest-scoped Codec Adapters.

Environment: macOS Tahoe 26.4.1, Apple M4 arm64, .NET SDK 10.0.102, runtime 10.0.2, Concurrent Workstation GC, BenchmarkDotNet 0.15.8. Five baseline/candidate runs were executed serially and the median of the five reported Means is used.

| Scenario | 0.7.10 median | 0.7.11 median | Candidate throughput | B/op baseline→candidate |
| --- | ---: | ---: | ---: | ---: |
| Adapter payload, 16 | 52.80 μs | 53.77 μs | 98.20% | 1152 → 1152 |
| Adapter payload, 256 | 54.48 μs | 55.26 μs | 98.59% | 5952 → 5952 |
| Native array, 16 | 52.84 μs | 52.49 μs | 100.67% | 440 → 440 |
| Native array, 256 | 52.71 μs | 53.01 μs | 99.43% | 1400 → 1400 |

BenchmarkDotNet iteration statistics are not RPC latency percentiles, so a separate TCP LoadTest measured real QPS/P99 with one connection, Balanced profile, request timeout disabled, c1/c128, one-second warmup, three-second measurement, and five alternating runs. All ten reports had zero failures.

| Concurrency | 0.7.10 QPS / P99 | 0.7.11 QPS / P99 | QPS ratio | P99 ratio |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 26,358.46 / 70 μs | 25,659.63 / 72 μs | 97.35% | 102.86% |
| 128 | 1,297,012.17 / 130 μs | 1,294,560.44 / 129 μs | 99.81% | 99.23% |

Every point passes the 97% QPS and 105% P99 gates. Benchmark allocations do not increase. Fixed MemoryPack 1.21.4 fixtures are byte-identical under SharpPack 1.0.1, supporting the unchanged `memorypack-binary/v1` identity.

These are local macOS arm64 results. No remote Windows/Linux CI or long-duration release matrix ran because this task explicitly forbids remote writes.
