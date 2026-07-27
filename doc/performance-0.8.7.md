# SharpLink 0.8.7 性能验证

English: [`en/performance-0.8.7.md`](en/performance-0.8.7.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8，1 launch、5 warmup、15 measurement iterations。正常 ClientConnection Dispose：0.8.6 为 1.145 µs（99.9% CI 1.143–1.148），0.8.7 为 1.146 µs（1.142–1.151），allocation 均 18.51 KB。两个分别增加 Task 与对象字段的早期设计因 18.56/18.58 KB allocation 被拒绝；最终实现复用 RpcSession teardown。原始报告位于 `artifacts/performance/0.8.7-client-dispose-ab/`。
