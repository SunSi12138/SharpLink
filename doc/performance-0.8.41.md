# SharpLink 0.8.41 性能验证

English: [`en/performance-0.8.41.md`](en/performance-0.8.41.md)

Apple M4 / .NET SDK 10.0.102 上，以 exact `dd431f5` detached checkout 与最终候选交错运行真实 TCP unary harness。每个进程预热 2,000 次，再对普通调用与一层 Client+Server pass-through interceptor 各取 9×20,000 次样本中位数；最终使用每侧五个独立进程的中位数。

| 构建 | 普通调用五进程中位数 | 进程中位数 | 分配 |
|---|---|---:|---:|
| 0.8.40 exact baseline | 38.498 / 39.792 / 38.270 / 39.129 / 38.694 µs | 38.694 µs | 320.01–320.04 B/op |
| 0.8.41 candidate | 37.285 / 37.652 / 38.832 / 39.312 / 39.200 µs | 38.832 µs | 320.01–320.02 B/op |

| 构建 | interceptor 五进程中位数 | 进程中位数 | 分配 |
|---|---|---:|---:|
| 0.8.40 exact baseline | 39.748 / 39.911 / 40.995 / 40.659 / 39.725 µs | 39.911 µs | 1,560.01–1,560.03 B/op |
| 0.8.41 candidate | 40.302 / 41.148 / 40.730 / 38.494 / 40.211 µs | 40.302 µs | 1,560.01–1,560.03 B/op |

普通路径变化 +0.36%，interceptor 路径变化 +0.98%；逐进程区间高度重叠，分配不变，没有可测回退。为直接覆盖新增的 reference-item nullability 分支，另对共享 dispatcher 执行每进程 15×2,000,000 次 dispatch/consume：exact baseline 三进程为 13.860 / 13.527 / 14.121 ns/op，candidate 为 13.860 / 13.871 / 13.643 ns/op，两侧进程中位数恰好均为 13.860 ns/op，分配均为 1.333 B/op。

原始 exact checkout、harness 与逐进程输出位于 `artifacts/performance/0.8.41-baseline/`、`artifacts/performance/0.8.41-unary-ab/` 和 `artifacts/performance/0.8.41-stream-ab/`。组合门禁通过非增量 Release 0 warning/error、Generator 120/120、Unit 490/490、Integration 250/250、120 秒共享内存 Chaos、NativeAOT TCP、七包 pack 与 fresh-cache TCP/shared-memory functional smoke。
