# SharpLink 0.8.25 性能验证

English: [`en/performance-0.8.25.md`](en/performance-0.8.25.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.24 commit `09d6078` 与最终候选在独立 Release 进程运行相同 Roslyn harness。输入包含 40 个 namespace 隔离的 RPC contract 与 400 个普通 instance method；5 轮预热后采集 101 个样本，每个样本前完成 GC 收敛。

基线中位数 15.953 ms（P25 12.323 / P75 23.967），候选 13.577 ms（P25 12.481 / P75 21.864），quartile 重合且未见 latency 回退。正常 only-method contract 的 abstract member 检查使用无分配早退；HashSet 仅在实际发现 property/event 时延迟创建，hint 后缀复用已有 contract ID，不重复计算 SHA。

同一 compiler thread 的中位分配为 28,454,352 → 28,495,328 B，增加 40,976 B（0.14%）。成本来自新增源码诊断/标识元数据；运行时 assembly、合法请求 codec 与 dispatch 热路径未修改。Harness 保留在 `artifacts/performance/0.8.25-generator-harness/`，基线 worktree 位于同一 task artifact 目录。
