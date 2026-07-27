# SharpLink 0.8.6 性能验证

English: [`en/performance-0.8.6.md`](en/performance-0.8.6.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8，1 launch、5 warmup、15 measurement iterations。正常 RpcSession Dispose：0.8.5 为 950.9 ns（99.9% CI 944.7–957.1），0.8.6 为 955.8 ns（949.9–961.6），区间重叠；allocation 均为 17.5 KB。其余改动仅在异常/终止冷路径。原始报告位于 `artifacts/performance/0.8.6-dispose-ab/`。
