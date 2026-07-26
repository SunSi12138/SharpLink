# SharpLink 0.8.13 性能验证

English: [`en/performance-0.8.13.md`](en/performance-0.8.13.md)

Apple M4 / .NET 10.0.2，以 0.8.12 commit `db20b9e` 和最终候选分别构建独立进程。为排除短跑中的分层 JIT 切换，两边统一关闭 tiered compilation，并以反向顺序各运行两轮、每个工作负载 12 个测量样本。

有数据可用的 Reader `ReadAsync`/`AdvanceTo` 基线中位数为 71.64/72.96 ns，候选为 72.36/70.93 ns；默认 token 的控制 pulse/wait 为 19.88/20.33 ns 对 20.03/20.39 ns，两项工作负载双方均为 0 B/op。无 spill 的正常 writer initialize/complete 基线为 65.54/70.31 ns，候选为 64.04/65.92 ns，且候选分配稳定由 280 B 降到 256 B，没有回退信号。

初始候选曾让默认等待增加约 1.2 ns，并让 writer 完成出现额外的常规路径成本；前者通过拆分可取消冷路径消除，后者通过恢复一次性完成的基线形状并把仅 spill 所需的 gate 收敛移到 no-inline 冷辅助方法消除。原始驱动与日志位于 `artifacts/performance/0.8.13-shared-memory-ab/`。
