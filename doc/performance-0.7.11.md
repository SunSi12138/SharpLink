# SharpLink 0.7.11 性能验证

English: [`en/performance-0.7.11.md`](en/performance-0.7.11.md)

## 环境与方法

- 基线：`dev` / `2dd4e84870b2694640ecd4ba61bec51f461e7226` / 0.7.10 / MemoryPack 1.21.4。
- 候选：0.7.11 / SharpPack 1.0.1 / manifest-scoped Codec Adapter。
- macOS Tahoe 26.4.1、Apple M4 arm64、10 cores。
- .NET SDK 10.0.102、Runtime 10.0.2、Concurrent Workstation GC。
- BenchmarkDotNet 0.15.8：4096 invocations、3 warmup、10 measurement iterations、1 launch。
- 基线/候选五轮串行交替，报告五轮 Mean 的中位数；单轮抖动不替代中位数结论。

## BenchmarkDotNet Unary

`Rpc_EchoPayload` 对比旧 MemoryPack 复杂 DTO 与新 SharpPack Adapter；`Rpc_SumArray` 检查原生 Codec 热路径。

| 场景 | 0.7.10 中位数 | 0.7.11 中位数 | 候选吞吐比例 | B/op 基线→候选 |
| --- | ---: | ---: | ---: | ---: |
| Adapter payload, 16 | 52.80 μs | 53.77 μs | 98.20% | 1152 → 1152 |
| Adapter payload, 256 | 54.48 μs | 55.26 μs | 98.59% | 5952 → 5952 |
| Native array, 16 | 52.84 μs | 52.49 μs | 100.67% | 440 → 440 |
| Native array, 256 | 52.71 μs | 53.01 μs | 99.43% | 1400 → 1400 |

四个点均高于 97% 吞吐门禁，分配中位数不增加。Adapter Scope 创建不在每调用路径；稳态生成 Codec 不做反射、全局锁或运行时 Type dictionary lookup。

原始报告位于任务审计目录 `.audit/benchmarks/alternating/`。第一次候选发现命令因嵌套基线目录产生无效结果，未计入五轮；随后从各 benchmark 项目目录执行的 candidate-1 至 candidate-5 均有效。

## TCP QPS/P99

BenchmarkDotNet 的 iteration 统计不等于真实 RPC P99，因此另外使用本地 TCP LoadTest。参数为 single connection、Balanced、request timeout disabled、`add`、c1/c128、1 秒 warmup、3 秒 measurement、五轮交替；十份报告 Failure 均为 0。

| 并发 | 0.7.10 QPS / P99 | 0.7.11 QPS / P99 | QPS 比例 | P99 比例 | 结论 |
| ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 26,358.46 / 70 μs | 25,659.63 / 72 μs | 97.35% | 102.86% | 通过 |
| 128 | 1,297,012.17 / 130 μs | 1,294,560.44 / 129 μs | 99.81% | 99.23% | 通过 |

QPS 均不低于 97%，P99 均不高于 105%。原始 JSON 和日志位于 `.audit/benchmarks/load-alternating/`。

## Wire payload 与结论

MemoryPack 1.21.4 与 SharpPack 1.0.1 的固定 fixtures 对 null root、nullable/string/非 ASCII、array/list/dictionary、nested、empty collection、union/polymorphism 和 circular reference 均 byte-for-byte 相同，SharpPack 显式 Context 能读取旧 payload。因此扩展声明保留 `memorypack-binary/v1`。

本地证据通过 97%/105% 性能门禁。该结果只代表本机 macOS arm64；本任务未授权 push，因此 Windows/Linux 远程 CI 和正式长稳矩阵未运行。
