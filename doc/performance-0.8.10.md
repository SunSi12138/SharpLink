# SharpLink 0.8.10 性能验证

English: [`en/performance-0.8.10.md`](en/performance-0.8.10.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8，1 launch、5 warmup、15 measurement iterations。交替复测的正常 Runtime Context 构建与释放：0.8.9 为 346.1 ns（99.9% CI 345.4–346.7），0.8.10 为 343.7 ns（342.7–344.8），allocation 均为 3.9 KB。一次未拆分冷路径的早期候选为 350.2 ns；最终将回滚聚合移入 no-inline 冷路径后未见回退。原始报告位于 `artifacts/performance/0.8.10-context-build-ab/`。
