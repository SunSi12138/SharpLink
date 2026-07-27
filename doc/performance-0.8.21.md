# SharpLink 0.8.21 性能验证

English: [`en/performance-0.8.21.md`](en/performance-0.8.21.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.20 commit `726992c` 与最终候选运行独立进程，关闭 tiered compilation，每个 workload 预热后运行 9 个样本，并用候选/基线交错顺序复验。

metadata 构造保持 136 B/op，候选与基线 median 均约 13 ns。两条 metadata 的 payload sizing 从约 15.1–16.2 ns 增至 17.0–18.9 ns；generated ASCII/Unicode string write 分别从约 10.6–10.9/15.5–16.0 ns 增至 15.0–15.3/19.6–20.0 ns，全部保持 0 B/op。绝对成本约 2–4 ns，仅发生在明确携带 metadata 或 generated string 的调用。

首版额外 surrogate scan 曾令短 ASCII 写入增至约 16.3 ns、metadata 构造增至约 26.7 ns，已否决。最终方案把合法性检查融合进本来必需的 UTF-8 byte-count/encode 过程，并保持 metadata snapshot 构造路径不变。这是防止业务字段和路由上下文静默数据损坏的有界完整性成本。原始驱动位于 `artifacts/performance/0.8.21-unicode-ab/`。
