# SharpLink 0.8.17 性能验证

English: [`en/performance-0.8.17.md`](en/performance-0.8.17.md)

Apple M4 / .NET 10.0.2，以 0.8.16 commit `0e4e1a7` 和最终候选构建运行独立进程，统一关闭 tiered compilation；每个 workload 9 个测量样本，共四组交错的候选→基线与基线→候选顺序。

四组 median 中，buffer pool rent/return 基线为 8.43–8.66 ns、候选为 8.34–9.06 ns，双方均 0 B/op；pending completion 为 45.23–46.42 ns 与 43.87–46.68 ns，均 48 B/op；flow-credit round trip 为 22.04–22.48 ns 与 21.13–22.07 ns，均 0 B/op；handshake request round trip 为 115.28–117.05 ns 与 113.56–118.75 ns，均 64 B/op。没有稳定热路径回退。

Runtime Context Build/Dispose 基线为 640.48–656.69 ns、候选为 638.44–674.62 ns，均 4048.13 B/op；Server Build/Stop 为 2.262–2.376 µs 与 2.259–2.410 µs，均 13224.81 B/op。TLS client options snapshot 因深复制 chain policy 从 96 B、12.32–13.29 ns 增至 184 B、82.15–83.95 ns；admission controller creation 因深复制 nested limits 从 1152 B、268.49–272.55 ns 增至 1224 B、273.50–280.89 ns。这两项固定成本只发生在安全配置/生命周期边界，用于隔离可变策略；运行时热路径分配不变。原始驱动位于 `artifacts/performance/0.8.17-negotiation-bounds-ab/`。
