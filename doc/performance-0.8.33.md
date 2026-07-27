# SharpLink 0.8.33 性能验证

English: [`en/performance-0.8.33.md`](en/performance-0.8.33.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.32 commit `2f3d27c` 与最终候选的独立 Release 进程对照。压力夹具包含 40 个契约、400 个 enum RPC 方法；每个进程预热 5 次，再运行 101 次并记录中位数与四分位区间。

| 版本 | Median | P25–P75 | 当前线程分配中位数 |
|---|---:|---:|---:|
| 0.8.32 baseline | 20.192 ms | 15.240–26.890 ms | 32,888,392 B |
| 0.8.33 final | 15.116 ms | 13.973–24.087 ms | 33,142,168 B |

候选延迟未回退且两个分布重叠；enum-heavy 极端夹具的分配增加 253,776 B（0.77%），来自每个非固定 enum size-field 的唯一后缀与略长生成文本。先行 SHA-256 方案曾分配 34,559,896 B（比基线约 +5.1%），因收益不足已撤销，最终改用现有确定性 64 位哈希。

Builder 变化只在同步 Build 失败回滚执行；Hosted 检查只在 Start 边界执行；Generator 变化不进入运行时 RPC、序列化或传输热路径。原始夹具与独立 0.8.32 worktree 保留在 `artifacts/performance/0.8.33-generator-ab/` 与 `artifacts/performance/0.8.33-baseline/`。
