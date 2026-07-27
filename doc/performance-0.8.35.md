# SharpLink 0.8.35 性能验证

English: [`en/performance-0.8.35.md`](en/performance-0.8.35.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.34 commit `044598c` 的独立 worktree 为基线。夹具每个样本构建并释放 1,000 个固定 transport Client，预热 100 次，每个独立进程测 21 个样本；基线和候选各重复三轮。

| 版本 | 三轮 Median | P25–P75（各轮） | 分配 |
|---|---|---|---:|
| 0.8.34 baseline | 2,449.7 / 2,450.2 / 2,562.6 ns | 2,382.9–2,561.7 / 2,382.0–2,538.3 / 2,421.5–2,612.7 ns | 6,536 B/op |
| 0.8.35 candidate | 2,370.2 / 2,267.4 / 2,202.4 ns | 2,191.8–2,461.5 / 2,194.9–2,343.3 / 2,149.2–2,257.8 ns | 6,168 B/op |

内部读取 frozen `PerformanceProfile` 使每次 Build 减少 368 B（5.63%）。候选三轮中位数都低于基线三轮区间；按三轮 median 的中位数比较为 2,450.2 → 2,267.4 ns，改善 7.46%。公共 `Options` 防御性深拷贝没有改变。

Resolver/断线日志分类、Chaos oracle、报告失败与协议 teardown 只进入控制或失败路径，不改变正常 RPC、序列化、transport 或 send-pump 热路径。原始夹具保留在 `artifacts/performance/0.8.35-context-profile-ab/` 与 `artifacts/performance/0.8.35-baseline/`。
