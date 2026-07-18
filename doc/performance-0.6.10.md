# SharpLink 0.6.10 取消与 Deadline 性能证据

## 环境与门禁

- 机器：Apple M4，10 physical/logical cores，Arm64。
- 系统：macOS 26.4.1。
- Runtime：.NET 10.0.2，Workstation Concurrent GC。
- 配置：Release；BenchmarkDotNet 10 次测量，LoadTest 使用 TCP/Balanced/连接池 1/1。
- 回归门禁：QPS 不低于 0.6.9 的 97%，P99 和 alloc/op 不高于 105%，错误不得增加。

## Deadline 实现选择

第一版使用每调用独立 deadline CTS，正确解决了“业务 Token 回调先于 Reason 发布”的问题，但 A/B 直接否决该实现：

| 场景 | 0.6.9 | 独立 deadline CTS 实验 | 变化 |
|---|---:|---:|---:|
| cooperative deadline | 320 B/op | 368 B/op | +15.0% |
| non-cooperative deadline | 320 B/op | 320 B/op | 0% |

cooperative 路径超过 105% allocation 门禁，因此实现被撤销。最终版本改为每物理连接一个 Timer，在到期时扫描已有的最多 1,024 项调用表。正常完成不维护 heap/timer node，也不进入 scheduler lock。

最终 `ServerCallCancellationStateBenchmarks`：

| 场景 | Mean | Allocated | 相对 0.6.9 allocation |
|---|---:|---:|---:|
| no deadline | 11.85 ns | 32 B/op | — |
| cooperative deadline | 41.27 ns | 80 B/op | -75.0% |
| non-cooperative deadline | 38.39 ns | 32 B/op | -90.0% |
| cancel + dispose | 18.29 ns | 80 B/op | — |

扫描快照从 `ArrayPool` 租借，只在 deadline timer 到期时发生；快照保存完整 request ID 和 state 引用，并通过生命周期 gate 获取使用权，防止旧扫描命中归池后的新租约。

## 正确性压力

- 客户端 100,000 次 Response/UserCancellation/Deadline 三方竞争，每次恰有一个终态赢家，pending 表最终为 0。
- 服务端 100,000 次 Cancel/Response/Deadline 竞争，外加 10,000 次 Stop/Connection 并发取消，未出现重复完成、池损坏或错误原因覆盖。
- 真实 TCP 集成执行 10,000 次 server stream early-break；pending、dispatcher、send credit 和连接继续健康。
- 无 Token 调用在客户端超时后分别覆盖最终成功与最终抛错；两种迟到结果都被抑制并由 observer 收敛。

## 五轮交替 LoadTest A/B

A 为 `7e03729`（v0.6.9），B 为 `f3b7fd3`（0.6.10 功能候选）。每份报告使用 2 秒预热、5 秒采样；奇数轮 A→B，偶数轮 B→A，分别取五轮中位数。70 份 JSON 共覆盖 7 类场景、c1/c8/c32/c128 和两个版本，所有结果 Failure=0。

| 场景 | 并发 | 0.6.9 QPS | 0.6.10 QPS | QPS 变化 | 0.6.9 P99 | 0.6.10 P99 | P99 变化 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Unary default timeout | 1 | 25,071.64 | 25,548.53 | +1.90% | 72 us | 71 us | -1.39% |
| Unary default timeout | 128 | 1,222,262.59 | 1,216,052.36 | -0.51% | 193 us | 188 us | -2.59% |
| Unary timeout disabled | 1 | 25,772.64 | 25,406.64 | -1.42% | 71 us | 73 us | +2.82% |
| Unary timeout disabled | 128 | 1,221,615.83 | 1,226,047.95 | +0.36% | 206 us | 211 us | +2.43% |
| `Task.Yield()` service | 1 | 23,648.47 | 23,849.21 | +0.85% | 75 us | 75 us | 0% |
| `Task.Yield()` service | 128 | 493,634.74 | 545,043.65 | +10.41% | 584 us | 572 us | -2.05% |
| 1 ms async service | 1 | 704.29 | 696.43 | -1.12% | 1,620 us | 1,623 us | +0.19% |
| 1 ms async service | 128 | 83,767.46 | 84,944.34 | +1.40% | 2,720 us | 2,270 us | -16.54% |
| normal server stream | 1 | 7,961.16 | 8,339.25 | +4.75% | 206 us | 215 us | +4.37% |
| normal server stream | 32 | 9,545.10 | 9,417.02 | -1.34% | 3,860 us | 3,991 us | +3.39% |
| early-break stream | 1 | 14,857.74 | 14,953.81 | +0.65% | 127 us | 125 us | -1.57% |
| early-break stream | 32 | 26,141.53 | 27,140.38 | +3.82% | 2,499 us | 2,141 us | -14.33% |
| slow consumer stream | 1 | 3.00 | 3.00 | -0.01% | 335,006 us | 334,124 us | -0.26% |
| slow consumer stream | 128 | 358.17 | 358.21 | +0.01% | 350,822 us | 348,748 us | -0.59% |

短样本中 28 个“场景 × 并发”点有 25 个直接通过。normal c128 QPS、early-break c128 P99 与 slow c8 QPS 因五秒高方差或完成次数过少越过门禁，随后仅针对这三点使用 5 秒预热、20 秒采样重做五轮交替确认：

| 复核场景 | 0.6.9 QPS | 0.6.10 QPS | QPS 变化 | 0.6.9 P99 | 0.6.10 P99 | P99 变化 |
|---|---:|---:|---:|---:|---:|---:|
| normal stream c128 | 9,721.72 | 10,751.26 | +10.59% | 14,922 us | 13,193 us | -11.59% |
| early-break stream c128 | 27,615.68 | 27,686.98 | +0.26% | 6,980 us | 7,125 us | +2.08% |
| slow consumer c8 | 24.00 | 24.00 | 0% | 339,253 us | 338,218 us | -0.31% |

三个复核点全部通过 97%/105% 门禁。没有根据首轮噪声修改实现或放宽阈值。

## Unary allocation

`UnaryBenchmarks.Rpc_Add` 的 ShortRun 在 16 B 与 256 B payload 下均为 672 B/op，与 0.6.9 已接受基线相同。固定 1,024 invocation job 分别为 672 B/op 与 682 B/op，后者相对 0.6.9 的 674–675 B/op 增加约 1%，仍在 105% 门禁内。

Release Gate 竞态修复提交 `f8a86a0` 再次执行 ShortRun 与固定 1,024 invocation job，16 B/256 B 四个结果均为 672 B/op。Ready snapshot 判定与取消 dispatch drain 没有增加正常 Unary allocation。

针对 Ready 判定正常分支，以 `7e03729` 为 A、`f8a86a0` 为 B 再执行五轮交替 TCP/Balanced/pool 1/1，2 秒预热、5 秒采样，取中位数：

| 并发 | 0.6.9 QPS | 最终 0.6.10 QPS | QPS 变化 | 0.6.9 P99 | 最终 0.6.10 P99 | P99 变化 |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 25,959.81 | 26,150.94 | +0.74% | 70 us | 70 us | 0% |
| 128 | 1,246,368.22 | 1,227,205.53 | -1.54% | 150 us | 148 us | -1.33% |

十份报告 Failure 均为 0，QPS/P99 全部通过 97%/105% 门禁。

## 本地发布 Gate

- Release 全解决方案构建：0 warning / 0 error。
- Unit 157、Generator 17、Integration 83：全部通过；Integration 额外连续运行 10 轮均通过。
- macOS arm64 NativeAOT publish/run：`AOT_SMOKE_PASS`，无 AOT/trimming 警告。
- 0.6.10 正式包与 PackageSmoke：通过；`SharpLink.Sdk` 包含 Generator Analyzer。
- 最终修复提交 `f8a86a0` 的 120 秒混合 Chaos：3,027,164 success、2,749,080 injected、10 次滚动重启、0 unexpected；最大恢复 8.095 秒，结束时 connections/calls/pending/streams/send queue 全部为 0。

两分钟 retained memory 只包含启动和对象池预热，不适用六小时增长门禁。连续 24 小时 release soak 必须在最终 release commit 上单独执行，不能用短样本替代。
