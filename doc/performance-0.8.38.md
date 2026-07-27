# SharpLink 0.8.38 性能验证

English: [`en/performance-0.8.38.md`](en/performance-0.8.38.md)

本批 Generator 只增加无效模型的早期拒绝；Runtime 只改变异常路径中的取消分类，正常无 interceptor 路径完全未改。性能门禁同时覆盖生成构建与启用 interceptor 的成功调用路径。

在 Apple M4 / .NET SDK 10.0.102 上，使用 exact `576fbe3` detached worktree 与候选交错执行同一个 HostApplication 非增量 Release build，各五次：

| 构建 | 五次 wall time | 中位数 |
|---|---|---:|
| 0.8.37 exact baseline | 4.94 / 2.16 / 1.97 / 1.88 / 1.94 s | 1.97 s |
| 0.8.38 candidate | 2.50 / 1.92 / 1.95 / 1.83 / 1.89 s | 1.92 s |

候选中位数下降 2.5%，没有 Generator/build 回退。首对包含独立 worktree 冷缓存成本，但排除首对仍不改变结论。

复用真实 TCP unary harness，每个进程对普通路径与一层 Client+Server pass-through interceptor 各 warmup 2,000 次，再取 9×20,000 次样本中位数。相关 interceptor 路径的三进程中位数为：

| 构建 | 三个进程中位数 | 进程中位数 | 分配 |
|---|---|---:|---:|
| 0.8.37 exact baseline | 39.672 / 46.857 / 41.848 µs | 41.848 µs | 1,584.03 B/op |
| 0.8.38 candidate | 36.876 / 46.177 / 41.831 µs | 41.831 µs | 1,584.03 B/op |

延迟区间重叠，中心值基本不变（−0.04%），分配完全相同。原始 worktree、构建输出与 harness 位于 `artifacts/performance/0.8.38-baseline/` 和 `artifacts/performance/0.8.38-interceptor-ab/`。

组合门禁为非增量 Release 0 warning/error、Generator 117/117、Unit 483/483、Integration 241/241、120 秒共享内存 Chaos 和 NativeAOT TCP smoke。
