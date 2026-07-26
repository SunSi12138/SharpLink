# SharpLink 0.8.19 性能验证

English: [`en/performance-0.8.19.md`](en/performance-0.8.19.md)

Apple M4 / .NET 10.0.2，以 0.8.18 commit `1b380e6` 和最终候选构建运行独立进程，统一关闭 tiered compilation。每个 workload 先预热 2,000 次，再运行 9 个各 20,000 次 RPC 的样本，并使用候选→基线与基线→候选顺序交错复验。

无 interceptor 的 TCP unary RPC 在基线两轮 median 为 39.98/41.24 µs，候选为 39.29/38.98 µs；双方均约 320.01 B/op，样本区间交叠。默认路径没有新增 guard、分配或稳定延迟回退。

启用一个 Client 与一个 Server pass-through interceptor 时，基线 median 为 40.75/41.17 µs、候选为 40.04/40.93 µs，样本区间同样交叠。基线为 1552.01 B/op，候选为 1584.01 B/op；固定增加的 32 B 是两个一次性 continuation guard（每端 16 B），只在明确启用 interceptor 的调用上发生，用于阻止重复或并发 `next` 执行业务终点。原始驱动位于 `artifacts/performance/0.8.19-interceptor-ab/`。
