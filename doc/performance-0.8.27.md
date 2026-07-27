# SharpLink 0.8.27 性能验证

English: [`en/performance-0.8.27.md`](en/performance-0.8.27.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.26 commit `1d8325e` 与最终候选在独立 Release 进程运行相同 runtime harness。每项预热 20,000 次后采集 15 个样本；writer 租还执行 5,000,000 次/样本，pending Int32 response 执行 1,000,000 次/样本，stream dispatch/consume 执行 2,000,000 次/样本。

writer pool rent/return 中位数为 8.884 → 8.830 ns（P25/P75 8.583/9.873 → 8.541/9.500），分配均为 0 B/op。enqueue 后的 ownership 复核没有造成可测回退。

pending Int32 response completion 为 44.174 → 43.836 ns（P25/P75 42.531/45.075 → 42.532/45.977），分配均为 24 B/op。stream dispatch/consume 为 16.795 → 16.803 ns（P25/P75 15.059/17.315 → 16.353/17.613），分配均为摊销 1.333 B/op；区间重合，新增第二 token 分支没有可测回退。Harness 保留在 `artifacts/performance/0.8.27-runtime-ab/`，基线 worktree 位于 `artifacts/performance/0.8.27-baseline/`。
