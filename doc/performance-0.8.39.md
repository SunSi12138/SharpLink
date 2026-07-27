# SharpLink 0.8.39 性能验证

English: [`en/performance-0.8.39.md`](en/performance-0.8.39.md)

本批只有启用 interceptor 的成功调用路径新增结果类型和 continuation 完成校验；无 interceptor 路径未改。Apple M4 / .NET SDK 10.0.102 上，以 exact `d863dc3` detached worktree 和候选交错运行真实 TCP unary harness。每个进程分别预热普通与一层 Client+Server pass-through interceptor 2,000 次，再各取 9×20,000 次样本中位数。

| 构建 | interceptor 三个进程中位数 | 进程中位数 | 分配 |
|---|---|---:|---:|
| 0.8.38 exact baseline | 40.338 / 46.635 / 41.267 µs | 41.267 µs | 1,584.02–1,584.05 B/op |
| 0.8.39 candidate | 33.620 / 47.044 / 40.831 µs | 40.831 µs | 1,584.02–1,584.05 B/op |

延迟区间重叠，三进程中位数下降 1.06%，逐轮分配一致，没有可测回退。普通无 interceptor 路径的进程中位数也在噪声范围内，且其生产代码未改。原始 worktree、harness 与逐进程输出位于 `artifacts/performance/0.8.39-baseline/` 和 `artifacts/performance/0.8.39-interceptor-ab/`。

组合门禁通过非增量 Release 0 warning/error、Generator 118/118、Unit 484/484、Integration 246/246、120 秒共享内存 Chaos、NativeAOT TCP、七包 pack 与 fresh-cache TCP/shared-memory functional smoke。
