# SharpLink 0.8.3 性能验证

English: [`en/performance-0.8.3.md`](en/performance-0.8.3.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8；0.8.2 基线与 0.8.3 候选各使用 3 launch、3 warmup、10 measurement iterations。

| Benchmark | 0.8.2 | 0.8.3 | 延迟比例 | Allocation |
| --- | ---: | ---: | ---: | ---: |
| ConstructTwoEntries（控制） | 10.47 ns | 10.13 ns | 96.75% | 80 → 80 B |
| DecodeTwoEntries | 68.33 ns | 61.89 ns | 90.58% | 280 → 224 B (-20.0%) |

候选删除的 56 B 与两项 `KeyValuePair<string,string>[]` 大小吻合；构造控制项无分配回退，decode 延迟改善。原始报告位于 `artifacts/performance/0.8.3-metadata/`。
