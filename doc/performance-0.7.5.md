# SharpLink 0.7.5 本地性能报告

本报告记录静态 endpoint 功能完成前的本地开发证据。它不是跨平台发布声明；完整的 full matrix 由 `eng/run-v075-static-performance-matrix.sh` 在固定 runner 上重跑。

## 固定单 endpoint 门禁

- 基线：0.7.4 `faac6833fce06b9464f67f7a977ca63b592395c2`。
- 候选：静态 endpoint 实现完成后的本地提交 `8ea307296757880d4738af648a17b1783ad3edd2`。
- 环境：macOS Arm64、Apple M4、.NET 10.0.2、Release、TCP、本地 server/client 同进程、连接池 1/1、Unary `Add`、并发 8。
- 方法：基线与候选交替五轮；每轮 warmup 1 秒、采样 3 秒。原始 JSON 位于本任务 checkout 的 `artifacts/performance/v0.7.5/`。

| Build | QPS r1..r5 | Median QPS | P99 us r1..r5 | Median P99 |
|---|---|---:|---|---:|
| 0.7.4 baseline | 170060.44, 171314.82, 168318.39, 168209.54, 169653.35 | 169653.35 | 64, 64, 70, 67, 70 | 67 |
| 0.7.5 fixed path | 170109.60, 172012.05, 168039.38, 165756.18, 170589.72 | 170109.60 | 71, 67, 71, 70, 65 | 70 |

结论：QPS 为基线的 100.27%，P99 为基线的 104.48%，满足固定路径 QPS 不低于 99%、P99 不高于 105% 的门槛。

BenchmarkDotNet `UnaryBenchmarks.Rpc_Add` 的 `MemoryDiagnoser` 在 payload 16 与 256 下均为 **352 B/op**，与 0.7.4 报告中的固定路径分配相同。LoadTest 的进程级 allocated-bytes 仅用于辅助诊断，不用于取代该每操作分配结论。

## 静态 endpoint smoke

- JIT：`eng/run-v075-static-performance-matrix.sh` 的 smoke tier 已运行，覆盖折叠的 1 endpoint、2 endpoint P2C/LeastPending、并发 1/8、payload 0/256 B；全部请求零失败。
- NativeAOT：本机 `osx-arm64` 发布的 `SharpLink.LoadTest` 已以 2 endpoint、RoundRobin、Unary `Add`、并发 1 运行成功，QPS 35671.15、P99 57 us、零失败。

full tier 额外覆盖 1/2/8/32 endpoint、并发 1/8/32/128、payload 0/32/256/4096/65536 B、四种内置策略，以及 JIT/NativeAOT。它应在没有竞争负载的固定 runner 上运行，作为发布前的完整矩阵证据。
