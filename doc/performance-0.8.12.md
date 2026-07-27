# SharpLink 0.8.12 性能验证

English: [`en/performance-0.8.12.md`](en/performance-0.8.12.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8，1 launch、5 warmup、15 measurement iterations，并交替复测正常 Build/Dispose。直接 Client 基线为 614.0/615.7 ns，最终候选为 614.8/622.2 ns；动态 Client 基线为 812.0/812.1 ns，候选为 817.4/811.2 ns，快慢方向随进程互换且 allocation 分别稳定为 6.37/7.38 KB，未形成稳定回退信号。Server 基线为 1386.2/1383.1 ns，候选为 1385.4/1374.4 ns，allocation 由 12.94 降为 12.88 KB。首个在 Client 正常分支内增加异常边界的方案测得 619.2/828.4 ns，已拒绝并改为外层 no-inline 冷回滚。原始报告位于 `artifacts/performance/0.8.12-builder-ab/`。
