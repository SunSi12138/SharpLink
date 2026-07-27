# SharpLink 0.8.5 性能验证

English: [`en/performance-0.8.5.md`](en/performance-0.8.5.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8；0.8.4 commit `a7f8a24` 与 0.8.5 候选使用 1 launch、5 warmup、15 measurement iterations。

| Benchmark | 0.8.4 | 0.8.5 | 延迟比例 | Allocation |
| --- | ---: | ---: | ---: | ---: |
| PublishedClientAccessorLookup | 1.457 ns | 1.483 ns | 101.78% | 0 → 0 B |

候选与基线的 99.9% confidence intervals 分别为 1.443–1.522 ns 与 1.404–1.510 ns，明显重叠，因此结果作为无性能回退证据而非差异声明。发布/停止使用低频 gate 修复竞态，已发布 client 的常规读取仍为无锁、零分配路径。本批其余变更只位于异常与回滚冷路径。原始报告保存在 `artifacts/performance/0.8.5-accessor-ab/`。
