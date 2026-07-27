# SharpLink 0.8.28 性能验证

English: [`en/performance-0.8.28.md`](en/performance-0.8.28.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.27 commit `656271b` 与最终候选在独立 Release 进程运行相同 harness。每项预热 20,000 次并采集 15 个样本。

合法 binary error 写出为 11.888 → 11.968 ns/op（P25/P75 11.836/18.188 → 11.932/12.325），两端均为 0 B/op，新增错误码 switch 没有可测回退。

配置冷路径中，Socket options clone 为 5.536 → 6.674 ns/op、均 56 B/op；TokenBucket + FixedWindow + SlidingWindow 三项合计 validation 为 3.335 → 4.826 ns/op、均 0 B/op。约 1.14 ns 与 1.49 ns 的固定新增成本只在配置冻结/验证时发生，用于把迟发 native/BCL 失败前移，不进入 RPC 调用、发送或接收热路径。

另以 candidate → baseline → candidate → baseline 交替启动复核运行时 harness。两次进程中位数的中心值分别为：writer rent/return 8.056 → 8.322 ns（+3.3%，0 B/op），pending Int32 response 38.531 → 38.217 ns（-0.8%，24 B/op），stream dispatch/consume 14.510 → 13.904 ns（-4.2%，摊销 1.333 B/op）。全部保持原分配并落在 5% 门禁内。顺序固定的首轮出现统一温度漂移，未作为结论数据。

边界 harness 保留在 `artifacts/performance/0.8.28-boundary-ab/`；运行时 harness 位于 `artifacts/performance/0.8.27-runtime-ab/`，共同使用 `artifacts/performance/0.8.27-baseline/` 基线 worktree。
