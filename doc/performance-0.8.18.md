# SharpLink 0.8.18 性能验证

English: [`en/performance-0.8.18.md`](en/performance-0.8.18.md)

Apple M4 / .NET 10.0.2，以 0.8.17 commit `f7d4b8d` 和最终候选构建运行独立进程，统一关闭 tiered compilation；每个 workload 9 个测量样本，共四组交错的候选→基线与基线→候选顺序。

四组 median 中，buffer pool rent/return 基线为 8.26–8.72 ns、候选为 8.20–8.76 ns，双方均 0 B/op；pending completion 为 44.72–46.52 ns 与 44.71–46.24 ns，均 48 B/op；flow-credit round trip 为 20.99–21.84 ns 与 21.64–21.90 ns，均 0 B/op。独立样本区间交叠，未观察到稳定热路径回退。

空 RpcSession Dispose 基线为 1.542–1.585 µs、候选为 1.512–1.589 µs，均 17,904 B/op；Runtime Context Build/Dispose 为 649.42–668.72 ns 与 633.59–667.03 ns，均 4048.13 B/op；Server Build/Stop 为 2.239–2.369 µs 与 2.234–2.342 µs，均 13224.81 B/op。单 request 双 stream terminal drain 从 1280 B 增至 1312 B，四组 median 为 246.20–254.87 ns 与 247.23–273.13 ns；这一个 32 B snapshot 只发生在终止路径，用于在 request lock 外调用用户 callback，并保证异常 callback 不阻断其余 owner。原始驱动位于 `artifacts/performance/0.8.18-host-drain-stream-ab/`。
