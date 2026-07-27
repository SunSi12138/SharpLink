# SharpLink 0.8.40 性能验证

English: [`en/performance-0.8.40.md`](en/performance-0.8.40.md)

Apple M4 / .NET SDK 10.0.102 上，以 exact `8fffab7` detached checkout 与最终候选交错运行真实 TCP unary harness。每个进程分别预热普通与一层 Client+Server pass-through interceptor 2,000 次，再各取 9×20,000 次样本中位数。

| 构建 | interceptor 三个进程中位数 | 进程中位数 | 分配 |
|---|---|---:|---:|
| 0.8.39 exact baseline | 39.478 / 40.752 / 39.845 µs | 39.845 µs | 1,584.02–1,584.04 B/op |
| 0.8.40 candidate | 38.997 / 40.234 / 40.298 µs | 40.234 µs | 1,560.01–1,560.02 B/op |

延迟区间重叠，三进程中位数变化 +0.98%，没有可测延迟回退；逐调用分配下降 24 B。无 interceptor 路径的进程中位数为 38.640 → 38.454 微秒（−0.48%），分配均约 320 B/op。`RpcMethodDescriptor` 通过 packed flags 从 exact baseline 的 48 B 降到 40 B，避免 response-nullability 元数据扩大每次 interceptor context。

原始 exact checkout、harness 与逐进程输出位于 `artifacts/performance/0.8.40-baseline/` 和 `artifacts/performance/0.8.40-interceptor-ab/`。组合门禁通过非增量 Release 0 warning/error、Generator 119/119、Unit 486/486、Integration 250/250、120 秒共享内存 Chaos、NativeAOT TCP、七包 pack 与 fresh-cache TCP/shared-memory functional smoke。
