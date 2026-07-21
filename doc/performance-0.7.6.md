# SharpLink 0.7.6 本地性能报告

本报告记录动态 endpoint/Resolver 完成后的本地开发证据。结果用于回归判断，不替代固定硬件、跨平台发布前的 full matrix。

## 固定单端点门禁

- 基线：本地 0.7.5 提交 `8b1a2da`。
- 候选：0.7.6 动态 Resolver 实现完成后的本地工作树。
- 方法：本机 TCP Unary `Add`、连接池 `1/1`、并发 8；基线和候选交替五轮，每轮 warmup 1 秒、采样 2 秒。原始 JSON 保存在本任务 checkout 的 `artifacts/performance/v0.7.6/`。

| 路径 | QPS（五轮） | QPS 中位数 | P99（µs，五轮） | P99 中位数 |
| --- | --- | ---: | --- | ---: |
| 0.7.5 基线 | 163903.11, 162824.50, 164070.70, 165479.71, 163517.55 | 163903.11 | 72, 75, 77, 72, 72 | 72 |
| 0.7.6 当前 | 165734.46, 167550.81, 163221.92, 164619.21, 163226.10 | 164619.21 | 70, 72, 72, 73, 74 | 72 |

固定路径 QPS 中位数为基线的 **100.44%**，P99 中位数保持 **72 µs**，满足 0.7.6 的固定路径门禁。

BenchmarkDotNet `UnaryBenchmarks.Rpc_Add`（Payload 16/256，`MemoryDiagnoser`）均为 **352 B/op**，与 0.7.5 记录一致。报告位于 `artifacts/benchmarks-v076/results/`。

## 动态 Resolver 稳态 smoke

`eng/run-v076-dynamic-performance-matrix.sh` 已在 JIT smoke tier 运行：1/2 endpoint、P2C/LeastPending、并发 1/8、payload 0/256 B，共 16 个真实本地 TCP case，全部零失败。它使用不变的 Delegate snapshot，确认 resolver 模式在没有新快照时不会周期性重建候选 topology。原始 JSON 位于 `artifacts/performance/v0.7.6/dynamic/`。

NativeAOT 已发布并运行 `SharpLink.AotSmoke` 的动态 TCP Resolver 路径，输出 `AOT_SMOKE_PASS transport=tcp`。

完整发布前矩阵可设置 `SHARPLINK_V076_MATRIX_TIER=full`；它扩展为 1/2/8/32 endpoint、并发 1/8/32/128、payload 0/32/256/4096/65536 B、四种内置策略，并可通过 `SHARPLINK_V076_MATRIX_RUNTIMES=jit,aot` 同时运行 NativeAOT。
