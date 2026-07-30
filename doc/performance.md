# SharpLink 性能基线

本文只发布 `1.0.0-rc6` 精确提交上的最终可复现实测。0.x 开发期的逐版本局部 A/B、临时 runner 结果和优化日志不属于稳定性能承诺，已从用户文档移除；代码历史仍保留在 Git 和 CHANGELOG。

## 当前状态

`1.0.0-rc6` 在 RC5 正确性基线上修复了 stream-backed transport 的系统性分段开销：默认 4 KiB PipeReader block 小于常见的 4096-byte 业务 payload 帧，使 SharpPack 经常进入跨段读取。RC6 使用经 A/B 接受的 16 KiB block；公开 API、协议、连接默认数和包表面不变。最终云端 QPS、吞吐和延迟数字仍必须在 RC6 精确发布提交完成下述矩阵后发布，本地优化 A/B 不冒充最终云端基线。

## 环境记录

最终报告必须包含：

- exact commit、版本和工作区是否干净；
- OS、内核、CPU 型号/核心、内存、架构与电源模式；
- .NET SDK/runtime、JIT/NativeAOT、GC 与 server GC 配置；
- transport、profile、payload、operation、connections、concurrency、warmup、duration、repetitions；
- compression/admission/interceptor/topology 配置；
- 同机后台进程与温控检查。

原始 JSON、BenchmarkDotNet 报告和环境快照写入 `artifacts/performance/rc6-<short-sha>/`，不提交仓库；本文只保存汇总表、命令和解释。

## RC6 场景矩阵

| 维度 | 覆盖 |
|---|---|
| Transport | TCP、UDS（适用平台）、NamedPipe、AnonymousPipe、SharedMemory |
| Runtime | JIT；支持的发布入口另跑 NativeAOT smoke/代表负载 |
| Profile | LowLatency、Balanced、Throughput |
| Unary payload | 0、32、256、4096、65536、1048576 B |
| Concurrency | 1、8、32、128、256、512 |
| Pool | 1/1 与 1/4；静态 cluster 另测 2/8 endpoints |
| Calls | Unary、async、OneWay、OneWay backpressure |
| Streams | c2s、s2c、duplex，报告业务 MiB/s 与 item QPS |
| Optional paths | Compression on/off、admission immediate/queued/rejected、interceptor on/off |
| Topology | fixed、static endpoints、stable resolver snapshot |

跨机器 TCP 另记录 client/server 两台机器、链路速率、MTU 和 RTT，不与本机 IPC 表混合。

## 运行命令

快速可运行性：

```bash
SHARPLINK_MATRIX_TIER=smoke ./eng/run-performance-matrix.sh
```

完整 RC 矩阵：

```bash
SHARPLINK_MATRIX_TIER=full \
SHARPLINK_MATRIX_RUNTIMES=jit,aot \
SHARPLINK_MATRIX_OUTPUT="$PWD/artifacts/performance/rc6-<short-sha>/matrix" \
./eng/run-performance-matrix.sh
```

参数和异机模式见 [loadtest.md](loadtest.md)。

## 统计规则

- 每个配置至少五个独立进程，交替或反转顺序，使用 process median 作为中心值并保留完整范围。
- 报告 QPS、业务吞吐、P50/P95/P99/P99.9、错误数、allocated bytes/op、Gen0/1/2、CPU time/op 和峰值工作集。
- 任一非注入错误、crash、timeout、资源未归零或后台异常都使该场景失败，不能只从成功样本计算性能。
- warmup 与 measurement 分离；不得在采样窗口同时运行 trace、详细 SharedMemory evidence 或其他负载。
- 不把不同日期/温度/电源状态的单次差异解释为优化。需要前后归因时，对基线和候选严格交替，并报告 paired median。

## 接受标准

- 所有 mandatory 场景零非注入失败。
- 相对冻结前可信基线无可复现的 QPS/吞吐下降或尾延迟/分配上升；阈值按场景噪声带和工程影响判断，不用单一百分比掩盖异常。
- SharedMemory 同时报告绝对结果及与适用本机 transports 的关系，不宣称对所有 payload/并发都更快。
- Optional feature 的代价单独列出，不能把关闭 feature 的数字冒充启用后的结果。

## 最终结果

最终云端矩阵在冻结 `1.0.0-rc6` 发布提交后填写。上云前的本机因果 A/B 仅用于决定是否接受 RC6 Runtime 改动：

| 固定连接数 | RC5 validated msg/s | RC6 候选 validated msg/s | 吞吐变化 | P99 变化 | CPU/msg 变化 | allocation/msg 变化 |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 105,513 | 134,910 | +27.86% | -24.78% | -22.00% | -2.96% |
| 4 | 303,388 | 374,602 | +23.47% | -8.17% | -12.72% | -3.06% |
| 16 | 569,858 | 592,295 | +3.94% | -4.83% | -4.38% | -0.42% |
| 64 | 626,257 | 633,006 | +1.08% | -9.26% | -2.72% | -0.30% |

环境为 Ryzen 9 7950X、Ubuntu 26.04、.NET 10.0.10、Server GC、TCP loopback、Throughput profile、c128、4096 bytes × 8 双向消息/流、固定 64 MiB send queue。每个连接数运行五对相邻 RC5/candidate 独立进程并交替 A/B 顺序，40 个进程均为零 transport/validation failure。原始证据对应 RC5 product `9a40218c73d51f470a54960a069df43c025cac78` 与 RC6 Runtime candidate `709471ab4ec2e67b714ad89eabec130f36925008`。

这项改动减少单条有序 transport lane 的分段成本，但不把一个 PipeReader 并行化。多 lane 扩展仍由 transport-independent connection pool 提供；未来 QUIC 或原生多路复用 transport 需要单独设计 session/lane 抽象，不能从本表推导为 RC6 承诺。
