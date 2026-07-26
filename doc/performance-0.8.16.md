# SharpLink 0.8.16 性能验证

English: [`en/performance-0.8.16.md`](en/performance-0.8.16.md)

Apple M4 / .NET 10.0.2，以 0.8.15 commit `8b6eeaa` 和最终候选构建运行独立进程，统一关闭 tiered compilation；每个 workload 9 个测量样本，并以候选→基线和基线→候选反向顺序复测。

最终两组反向样本中，buffer pool rent/return 基线为 8.79–8.85 ns、候选为 8.18–8.61 ns，32-byte packet 基线为 10.40–10.56 ns、候选为 10.19–10.33 ns，双方均 0 B/op。未修改的 pending register/complete 基线为 45.80–47.27 ns、候选为 44.86–46.01 ns，均 48 B/op；flow-credit round trip 基线为 21.67–22.33 ns、候选为 21.28–22.00 ns，均 0 B/op。没有稳定热路径回退。

Runtime Context Build/Dispose 基线为 636.32–651.93 ns、候选为 640.66–645.94 ns，均 4048.13 B/op；Server Build/Stop 基线为 2.267–2.376 µs、候选为 2.272–2.324 µs，均 13224.81 B/op。deadline 分片、Host lifetime、错误传播与容量验证均为异常或配置冷路径。原始驱动位于 `artifacts/performance/0.8.16-lifecycle-bounds-ab/`。
