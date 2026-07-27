# SharpLink 0.8.8 性能验证

English: [`en/performance-0.8.8.md`](en/performance-0.8.8.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8，1 launch、5 warmup、15 measurement iterations。正常匿名管道 offer 分配与释放：0.8.7 为 2.590 µs（99.9% CI 2.580–2.600），0.8.8 为 2.592 µs（2.583–2.601），allocation 均为 2.13 KB。区间重叠且分配不变；其他修改只在异常清理冷路径执行。一次因 benchmark 工程重名而在生成阶段被拒绝的启动没有产生测量数据，未计入结果。有效原始报告位于 `artifacts/performance/0.8.8-transport-dispose-ab/`。
