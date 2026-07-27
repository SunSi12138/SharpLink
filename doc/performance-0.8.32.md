# SharpLink 0.8.32 性能验证

English: [`en/performance-0.8.32.md`](en/performance-0.8.32.md)

Apple M4 / .NET SDK 10.0.102 / BenchmarkDotNet 0.15.8，以 0.8.31 commit `818f23e` 和最终候选的独立 Release 进程对照。每个进程 3 次 warmup、10 次 measurement、单次启动，测试现有 `AdmissionControllerBenchmarks.ImmediatePermit` 同口径场景。

| 版本 | Mean | 99.9% CI | Allocated |
|---|---:|---:|---:|
| 0.8.31 baseline | 58.477 ns | 58.265–58.688 ns | 568 B/op |
| 0.8.32 final | 49.262 ns | 49.005–49.519 ns | 288 B/op |

最终方案延迟约下降 15.8%，分配下降 280 B/op（约 49.3%），且置信区间完全分离。一个先行 `ArrayPool` 方案为 93.996 ns / 232 B/op：尽管分配更低，但比基线慢约 60.7%，已完整撤销。最终设计用精确 slot 数和单 lease 所有权消除常见路径的 retained/acquired 数组，不支付共享池租还、清零与分支成本。

UDS 修复只在 listener 异常清理执行；compression binding 与认证归一化只在 Build/握手执行；超大 timeout 仅替换已有 deadline 加法。默认未启用 admission 的 RPC 热路径不变。原始报告保留在 `artifacts/performance/0.8.32-baseline/` 与 `artifacts/performance/0.8.32-admission-candidate-project/`。
