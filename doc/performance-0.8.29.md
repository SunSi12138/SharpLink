# SharpLink 0.8.29 性能验证

English: [`en/performance-0.8.29.md`](en/performance-0.8.29.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.28 commit `a66eccc` 与最终候选在独立 Release 进程交替运行相同 harness。每项预热 20,000 次并采集 15 个样本，下列数据取两次进程中位数的中心值。

pending Int32 注册/响应从 37.176 → 37.127 ns/op（-0.1%），两端均为 24 B/op；Dispose 线性化所需的额外状态读取没有造成可测回退。多集群 Ready 状态读取从 8.972 ns / 56 B 降为 3.189 ns / 0 B，消除了轮询、指标和健康检查中的持续分配。

活动更新与 timeout elapsed 检查的合成微基准从 26.612 → 30.651 ns/op，增加 4.039 ns、两端均为 0 B/op。新增成本来自每次完整收帧记录一个 `Stopwatch` 时间戳，是避免墙钟跳变的固定正确性成本；heartbeat 检查本身是低频后台路径。它不进入 pending request、Codec、帧写出或发送路径，完整 pending 热路径 A/B 保持平稳。

外部复核将 Dispose/Rent 竞态运行 50,000 次而无残留；抽象 UDS 序列化字节完全一致；一百万次多集群 State 读取分配为 0。harness 与 0.8.28 基线 worktree 保留在 `artifacts/performance/0.8.29-hotpath-ab/` 和 `artifacts/performance/0.8.29-baseline/`。
