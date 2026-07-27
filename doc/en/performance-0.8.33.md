# SharpLink 0.8.33 performance validation

Chinese: [`../performance-0.8.33.md`](../performance-0.8.33.md)

Apple M4 / .NET SDK 10.0.102 compared independent Release processes at 0.8.32 commit `2f3d27c` and the final candidate. The stress fixture contains 40 contracts and 400 enum RPC methods; each process ran five warmups and 101 measured generations.

| Version | Median | P25–P75 | Median current-thread allocation |
|---|---:|---:|---:|
| 0.8.32 baseline | 20.192 ms | 15.240–26.890 ms | 32,888,392 B |
| 0.8.33 final | 15.116 ms | 13.973–24.087 ms | 33,142,168 B |

Candidate latency did not regress and the distributions overlap. The deliberately enum-heavy fixture allocates 253,776 B (0.77%) more for unique non-constant enum size-field suffixes and the longer generated text. An initial SHA-256 design allocated 34,559,896 B, about 5.1% above baseline, and was rejected; the final design reuses the deterministic 64-bit hash.

Builder changes run only during synchronous Build failure rollback, Hosted checks run only at Start, and Generator work does not enter RPC, serialization, or transport runtime paths. Raw artifacts are retained under `artifacts/performance/0.8.33-generator-ab/` and `artifacts/performance/0.8.33-baseline/`.
