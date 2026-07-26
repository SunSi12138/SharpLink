# SharpLink 0.8.9 性能验证

English: [`en/performance-0.8.9.md`](en/performance-0.8.9.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8，1 launch、5 warmup、15 measurement iterations。正常匿名管道 offer 分配与释放：0.8.8 为 2.576 µs（99.9% CI 2.563–2.589），0.8.9 为 2.597 µs（2.583–2.610），allocation 均为 2.13 KB，区间重叠。首版方案为 2.608 µs / 2.19 KB，因无条件 `Task`/锁分配被拒绝；最终方案只在真实异步或失败时缓存 `Task`。原始报告位于 `artifacts/performance/0.8.9-listener-dispose-ab/`。
