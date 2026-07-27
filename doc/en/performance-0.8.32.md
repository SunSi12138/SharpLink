# SharpLink 0.8.32 performance validation

Chinese: [`../performance-0.8.32.md`](../performance-0.8.32.md)

Apple M4 / .NET SDK 10.0.102 / BenchmarkDotNet 0.15.8 compared independent Release processes at 0.8.31 commit `818f23e` and the final candidate. Each ran the existing concurrency-only immediate-admission scenario with three warmups, ten measurements, and one launch.

| Version | Mean | 99.9% CI | Allocated |
|---|---:|---:|---:|
| 0.8.31 baseline | 58.477 ns | 58.265–58.688 ns | 568 B/op |
| 0.8.32 final | 49.262 ns | 49.005–49.519 ns | 288 B/op |

The final path is about 15.8% faster and allocates 280 B/op (49.3%) less, with disjoint confidence intervals. An earlier `ArrayPool` candidate reached 232 B/op but regressed to 93.996 ns, about 60.7% slower than baseline, so it was fully reverted. Exact slot sizing and direct single-lease ownership avoid retained/acquired arrays without pool rent/return, clearing, or branching costs.

The UDS change runs only during exceptional listener cleanup; compression bindings and authentication normalization run during Build/handshake; deadline saturation replaces existing timeout arithmetic. The default admission-disabled RPC path is unchanged. Raw artifacts remain under `artifacts/performance/0.8.32-baseline/` and `artifacts/performance/0.8.32-admission-candidate-project/`.
