# SharpLink 0.8.26 性能验证

English: [`en/performance-0.8.26.md`](en/performance-0.8.26.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.25 commit `0773496` 与最终候选在独立 Release 进程运行相同 Roslyn harness。输入包含 40 个 namespace 隔离的 RPC contract 与 400 个普通 instance method；5 轮预热后采集 101 个样本，每个样本前完成 GC 收敛。

基线中位数 14.755 ms（P25 12.534 / P75 22.503），首版候选为 16.241 ms，约回退 10%，因此未接受。合并 Oneway/Timeout attribute traversal 并移除局部变量命名中的捕获 lambda 后，最终候选为 13.530 ms（P25 12.703 / P75 18.953），通过 latency 门禁。

同一 compiler thread 的中位分配为 28,495,488 → 28,572,128 B，增加 76,640 B（0.27%）。新增成本受限于 Generator 分析；合法运行时请求路径不受影响。对 16-key 字典构造/插入单独比较原路径与 null guard，结果为 171.891 → 170.941 ns，未见运行时回退。Harness 保留在 `artifacts/performance/0.8.25-generator-harness/`，0.8.26 基线 worktree 位于 `artifacts/performance/0.8.26-baseline/`。
