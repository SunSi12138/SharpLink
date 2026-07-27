# SharpLink 0.8.4 性能验证

English: [`en/performance-0.8.4.md`](en/performance-0.8.4.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8；0.8.3 commit `fb1585e` 与 0.8.4 候选使用 1 launch、5 warmup、15 measurement iterations。

| Benchmark | 0.8.3 | 0.8.4 | 延迟比例 | Allocation |
| --- | ---: | ---: | ---: | ---: |
| CachedCodecLookup（explicit） | 6.529 ns | 6.533 ns | 100.06% | 0 → 0 B |
| CachedFallbackCodecLookup | 6.515 ns | 6.504 ns | 99.83% | 0 → 0 B |
| CachedGeneratedCodecLookup | 8.670 ns | 6.499 ns | 74.96% | 0 → 0 B |
| AttachedPreAdmissionDispatch | 17.656 ns | 17.098 ns | 96.84% | 0 → 0 B |

首个正确性实现把 attached dispatch 提高到 19.755 ns（+11.9%），因此未通过性能门。最终实现让 replay 期间的帧继续进入原有有界队列，排空后再原子发布 dispatcher；稳定热路径恢复原结构并略有改善。Codec cache 使用不持有 registration dictionary 的 snapshot identity，explicit/fallback 持平，同时 generated lookup 删除每次按 Type 查询 registration 的成本。原始报告保存在 `artifacts/performance/0.8.4-hotpath-ab/`。
