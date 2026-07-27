# SharpLink 0.8.24 性能验证

English: [`en/performance-0.8.24.md`](en/performance-0.8.24.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.23 commit `3202bd7` 与最终候选在独立 Release 进程运行同一 Roslyn harness。输入包含 20 个 RPC contract、400 个带合法 `[Timeout]` 的方法和 200 个合法 union case；最终数据在 5 轮预热后采集 101 个样本，并在每个样本前完成 GC 收敛。

初版为 timeout 另建一条 `SyntaxProvider` 分析管线，15-sample 中位数从 57.290 ms 增至 69.062 ms（约 20.5%），因此否决。最终实现把 `SHARPLINK050` 并入已有 invalid-method 遍历，并对常见直接 union 关系采用 symbol 快路径。101-sample 最终对照为 41.029 ms（P25 38.638 / P75 53.928）→ 40.675 ms（P25 38.796 / P75 50.502），未见 latency 回退。

同一 compiler thread 的中位分配为 27,019,920 → 27,175,096 B，每次包含 400 个 timeout 与 200 个 union 验证，增加 155,176 B（0.57%）。这是编译期诊断元数据成本；运行时源码与合法 RPC serialize/dispatch 热路径均未改变。Harness 保留在 `artifacts/performance/0.8.24-generator-harness/`，基线 worktree 位于同一 task artifact 目录。
