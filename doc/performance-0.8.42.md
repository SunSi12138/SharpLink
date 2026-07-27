# SharpLink 0.8.42 性能验证

English: [`en/performance-0.8.42.md`](en/performance-0.8.42.md)

Apple M4 / .NET SDK 10.0.102 上，以 exact 0.8.41 commit `d0e0df4` detached checkout 与最终候选执行独立进程 A/B。

## 真实 TCP unary

Balanced profile、单连接、并发 8，每进程预热 2 秒并测量 5 秒；十个基线与十个候选进程采用前五组 baseline-first、后五组 candidate-first 抵消顺序偏差。

| 指标 | 0.8.41 exact | 0.8.42 candidate | 变化 |
|---|---:|---:|---:|
| QPS 中位数 | 166,576 | 165,315 | -0.76% |
| P50 | 48 µs | 48 µs | 0 |
| P99 | 约 72 µs | 72 µs | 稳定 |
| allocation | 172.08 B/call | 171.98 B/call | -0.06% |

## Nullable Codec

每个进程对 `int?` contiguous payload 执行 15×3,000,000 次解码，取五个交替 A/B 进程中位数。

| 路径 | 0.8.41 exact | 0.8.42 candidate | 分配 |
|---|---:|---:|---:|
| present | 5.155 ns/op | 5.090 ns/op | 0 B/op |
| canonical null | 5.444 ns/op | 5.937 ns/op | 0 B/op |

canonical null 为验证固定值体多一次宽读取，绝对成本 0.493 ns；present 热路径没有回退。曾验证统一 decorator 方案会把 present/null 拉到约 10.3/21.5 ns，已拒绝并改为现有 Codec 分支内联。

## Throughput 稳定性

exact 0.8.41 的 TCP `operation=all` 独立复核两次均在 s2c warmup 以退出码 134 崩溃；聚焦样本中 s2c 3/5、c2s 5/5 崩溃。修复后 16/16 独立进程完成全部 64 个 unary/c2s/s2c/duplex 阶段且 failure 为零。原始 A/B、microbenchmark 与最终负载输出保存在 `artifacts/performance/0.8.42-*` 和 `artifacts/0.8.42-sendpump-repro/`。

组合门禁通过非增量 Release 0 warning/error、Generator 121/121、Unit 493/493、Integration 250/250、120 秒共享内存 Chaos、NativeAOT TCP、七包 pack 与 fresh-cache TCP/shared-memory functional smoke。
