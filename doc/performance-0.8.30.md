# SharpLink 0.8.30 性能验证

English: [`en/performance-0.8.30.md`](en/performance-0.8.30.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.29 commit `88039d5` 和最终候选在独立 Release 进程交替 A/B。

40 contract / 400 method Roslyn harness 每进程预热 5 轮并采集 101 个样本。最初把 `ReturnsValueTask` 加入增量 record 的方案出现约 8% 延迟回退，已拒绝；最终改为不参与增量等价/hash 的计算属性。三次进程中位数的中位值为 15.438 → 15.411 ms（-0.2%），同轮 compiler-thread 分配 28,570,544 → 28,570,408 B，未见回退。

本地 health-check harness 每项预热 20,000 次、采集 15 个两百万次调用样本，并循环 Ready/Draining/Unhealthy 防止常量状态特化。分配从 96 → 0 B/call。Apple Silicon 调度使候选延迟呈约 2/12 ns 双峰、基线呈约 7/13 ns 区间；最坏进程中位数约增加 5 ns，但四分位区间重叠，且该路径每次外部健康轮询只调用一次。这里接受明确的零持续 GC 压力收益，不宣称纯 latency 提升；RPC、Codec、transport I/O 热路径均未改变。

Harness 与基线 worktree 保留在 `artifacts/performance/0.8.30-generator-ab/`、`artifacts/performance/0.8.30-health-ab/` 和 `artifacts/performance/0.8.30-baseline/`。
