# SharpLink 0.8.15 性能验证

English: [`en/performance-0.8.15.md`](en/performance-0.8.15.md)

Apple M4 / .NET 10.0.2，以 0.8.14 commit `b32f846` 和最终候选构建独立进程；双方统一关闭 tiered compilation，并以反向顺序重复运行，每个 workload 9 个测量样本。

未修改的 flow-credit acquire/update 基线两轮为 22.05/21.39 ns、候选为 21.00/21.70 ns，均 0 B/op；pending register/complete 基线为 45.31/44.41 ns、候选为 45.07/45.52 ns，方向随进程顺序切换且均为 48 B/op，没有 RPC 热路径回退。Direct Client Build/Stop 保持 6576.4 B/op，基线/候选重复中位数分别位于约 1.17–1.21/1.19–1.24 µs；Server Build/Stop 保持 13224.8 B/op，基线/候选约 2.30–2.36/2.28–2.35 µs。两者都是配置冷路径且分配不变。

安全快照位于配置冷路径并有明确成本：已知 IP endpoint 的 factory 构造从 25.47–28.75 ns / 256 B 增至 38.95–40.93 ns / 360 B；经内置 endpoint delegate 创建从 47.62–51.38 ns / 240 B 增至 55.98–65.39 ns / 344 B；delegate 自身一次性创建从 7.05–7.32 ns / 88 B 增至 11.71–12.17 ns / 144 B。它们不会进入连接后 RPC 调用，增加的 104 B endpoint 快照和 56 B delegate 快照换取不可变配置与确定所有权。原始驱动位于 `artifacts/performance/0.8.15-configuration-ownership-ab/`。
