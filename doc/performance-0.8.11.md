# SharpLink 0.8.11 性能验证

English: [`en/performance-0.8.11.md`](en/performance-0.8.11.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8，1 launch、5 warmup、15 measurement iterations，并以相反顺序复测。正常 Client 注册/注销两轮均值由 0.8.10 的 6.535 µs 变为 0.8.11 的 6.518 µs，allocation 由 30.50 降为 30.44 KB。Server 两轮均值均为 6.407 µs；反向复测 allocation 在两版均为 29.52 KB，首轮跨进程摆动为 29.46/29.58 KB，未形成稳定回退信号。新增聚合对象仅位于异常回滚路径。原始报告位于 `artifacts/performance/0.8.11-dynamic-registration-ab/`。
