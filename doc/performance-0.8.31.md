# SharpLink 0.8.31 性能验证

English: [`en/performance-0.8.31.md`](en/performance-0.8.31.md)

Apple M4 / .NET SDK 10.0.102 / BenchmarkDotNet 0.15.8，以 0.8.30 commit `6ecdac9` 与最终候选的独立 Release 进程对照。每个进程 3 次 warmup、10 次 measurement、单次启动。

最初给公共 `ProtocolV2FrameToken` 增加 writer identity 的候选为 3.652 ns，冷 throw 优化为 3.563 ns，owner-driven 单参数 API 为 3.526–3.535 ns，均因相对 3.446 ns 初始基线存在可测回退而拒绝。最终方案逐字恢复原始 `BeginFrame`/`EndFrame` 方法体，只把重复 raw writer/token 收回 internal。

同一时段复跑的 0.8.30 基线为 3.473 ns（99.9% CI 3.454–3.492），最终 0.8.31 为 3.524 ns（3.509–3.540），约 +1.5%，双方均为 0 B/op，低于纳秒级 microbenchmark 的 5% 门禁。方法体没有生产代码差异，因此该小幅独立进程偏移不构成接受额外热路径工作的依据；永久 `RuntimeHotPathBenchmarks.WriteRequestFrame` 会继续守住该路径。

custom endpoint snapshot 仅在 factory 构造执行；Unix identity/preservation 仅在 listener 绑定/释放执行；anonymous-pipe transfer completion 每个外部子进程 offer 执行一次；API 删除/可见性调整不增加运行时代码。RPC、Codec、packet、session 与 transport I/O 稳态路径均未增加分配或分支。

基线 worktree、被拒方案和原始报告保留在 `artifacts/performance/0.8.31-baseline/` 与 `artifacts/performance/0.8.31-frame-writer-candidate/`。
