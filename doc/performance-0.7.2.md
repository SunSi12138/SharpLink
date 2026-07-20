# SharpLink 0.7.2 性能恢复报告

## 口径与环境

这里的“百万并发”经 0.3.0 原生 LoadTest 复核，含义是每秒百万次完成调用（QPS/ops/s），不是百万个同时在途调用。正式门禁使用静态 `Singleton` Unary `add`、单连接、Balanced profile、c128/c512；同机交替 0.7.1 与候选五轮，报告中位数，原始 JSON 保留最小值、最大值和离散程度。

- 机器：Apple M4，10 核，16 GiB，arm64；macOS 26.4.1 (25E253)
- SDK/Runtime：.NET SDK 10.0.102，Runtime 10.0.2；Release、Workstation GC、Interactive latency mode
- 电源：交流电、100% 电量；测试期间无 thermal/performance warning
- 基线：0.3.0 `729e123f6ccb4d70aa9127468611d352644e3c7d`；0.7.1 `38afb69cd26d83239626bc879b8fd49cf803b18e`
- 候选实现：`7b9d309`（Unary 直接 ValueTask）与 `a5aa3f7`（无节点分配请求池）
- 0.3.0 数据是在相同现代 .NET 10 Runtime 上运行其原生 LoadTest 的代码对比，不宣称是历史 SDK/Runtime 的逐字节复现。0.3.0 不含 SharedMemory；其 AnonymousPipe 原生 harness 在启动客户端前挂起，记为不支持。

原始报告、逐轮 JSON、Trace 和 BenchmarkDotNet 输出位于已忽略目录 `artifacts/performance/v0.7.2/`，不提交仓库。正式运行前均确认没有其他 LoadTest、StreamLoadTest、Chaos、Benchmark 或 Trace 进程竞争资源；后段 AnonymousPipe 样本受 WindowServer/UURemote 波动影响，CV 最高约 11.9%，因此仍以五轮中位数和范围判断。

## 历史回退定位

性能二分不是单一提交回退，而是两个功能阶段的累计成本：

| 版本/提交 | UDS add c128 / c512 | 归因 |
| --- | ---: | --- |
| 0.3.0 / `02a0e37` 附近 | 1.595M / 1.968M | 旧的轻量 Unary 路径；两点结果接近 |
| `8ad8af0` | 1.451M / 1.786M | 认证错误模型、调用上下文和稳定性边界加入后的第一段下降 |
| 0.4.0 `3d2c422` | 1.171M / 1.600M | Protocol v2、SendPump、deadline、资源上限和统一运行时后的第二段下降 |
| 0.5.0–0.7.1 | 约 1.16M–1.45M | 取消、流控、遥测、生命周期、动态程序集和安全排空继续保留必要成本 |

CPU sampling 中，`Monitor.Enter_Slowpath` 从 0.3.0 的约 0.58% 增至 0.7.1 的约 10.05%；其中约 7.8% 位于 Channel 多生产者 `SendPump.TryEnqueue` 下。最终候选相同采样约为 8.46% 与 6.99%。该竞争仍是后续可优化项，但替换唤醒或改变 Channel 延续策略的实验会显著降低 QPS，不能在没有等价正确性的实现时移除。

GC Allocation Trace 把 0.7.1 的固定调用成本定位到 `SharpLinkCallContextSnapshot`、`ExecutionContext`、`OneElementAsyncLocalValueMap`，以及两个 `ConcurrentStack<T>.Node`。前一组维持服务端认证/授权上下文跨异步调用可见；后一组可以安全消除。

## 已采用优化

1. 默认非 `WaitForReady` Unary 不再进入只负责等待响应的 async 包装方法，直接返回池化 `RpcRequestOperation<T>.AsValueTask()`。发送前后的同步异常仍以 faulted `ValueTask` 暴露，发送失败、取消、deadline、断连和响应完成继续经过原有单一仲裁。
2. `RpcRequestOperation<T>` 与 PendingCall 的客户端对象池由 `ConcurrentStack` 改为 `ConcurrentQueue`，消除每次归还创建的两个 Stack Node；池仍有界，回收仍清除 continuation 和请求引用。
3. 性能 runner 的 full tier 增加 c256/c512，BenchmarkDotNet 延长单次迭代，避免把过短迭代或负载发生器瓶颈当作框架结果。

BenchmarkDotNet 的同源代码、同作业精确分配结果：0.7.1 `Rpc_Add` 在 16 B/256 B 参数下均为 672 B/op；最终候选分别为 360 B/op、364 B/op。直接 ValueTask 中间候选为 424 B/op，说明无节点池进一步减少约 64 B/op。这里不以 0.3.0 分配为基线。

## 五轮正式 A/B

下表为五轮中位数。B/op 是整个 LoadTest 进程在该 stage 的分配增量除以成功调用数；所有 stage 均为零失败。

| Transport | c | 0.7.1 QPS / P99 / B/op | 0.7.2 QPS / P99 / B/op | QPS 变化 |
| --- | ---: | ---: | ---: | ---: |
| SharedMemory | 128 | 1,147,171 / 158 us / 520.4 | 1,273,182 / 150 us / 166.2 | +10.99% |
| SharedMemory | 512 | 1,248,244 / 956 us / 513.2 | 1,294,284 / 584 us / 164.7 | +3.69% |
| UDS | 128 | 1,322,664 / 141 us / 482.9 | 1,423,367 / 122 us / 138.2 | +7.61% |
| UDS | 512 | 1,460,490 / 956 us / 482.8 | 1,621,230 / 634 us / 138.5 | +11.01% |
| Named Pipe | 128 | 1,244,446 / 186 us / 482.9 | 1,369,891 / 155 us / 138.6 | +10.08% |
| Named Pipe | 512 | 1,346,430 / 1,201 us / 483.9 | 1,490,533 / 616 us / 140.0 | +10.70% |
| Anonymous Pipe | 128 | 1,334,027 / 174 us / 484.6 | 1,437,603 / 150 us / 139.8 | +7.76% |
| Anonymous Pipe | 512 | 1,407,569 / 1,183 us / 483.5 | 1,532,447 / 659 us / 139.1 | +8.87% |
| TCP loopback | 128 | 1,100,978 / 203 us / 482.4 | 1,201,835 / 168 us / 138.2 | +9.16% |
| TCP loopback | 512 | 1,265,971 / 1,445 us / 481.7 | 1,431,802 / 805 us / 137.6 | +13.10% |

SharedMemory 的两点均高于 0.7.1 的 99% QPS 门槛，P99 没有恶化；所有其他本地传输也有明确提升。候选五轮 QPS 范围为：SharedMemory 1.199M–1.420M、UDS 1.340M–1.649M、Named Pipe 1.357M–1.516M、Anonymous Pipe 1.226M–1.620M、TCP 1.159M–1.464M。P999 同样均优于各自 0.7.1 中位数。

## 与 0.3.0 的同机吞吐对比

0.3.0 仅用于吞吐与延迟的历史代码对比，不用于分配门槛。

| Transport | c | 0.3.0 QPS / P99 | 0.7.2 QPS / P99 | 恢复比例 |
| --- | ---: | ---: | ---: | ---: |
| UDS | 128 | 1,658,656 / 145 us | 1,423,367 / 122 us | 85.8% |
| UDS | 512 | 1,928,494 / 1,030 us | 1,621,230 / 634 us | 84.1% |
| Named Pipe | 128 | 1,567,043 / 164 us | 1,369,891 / 155 us | 87.4% |
| Named Pipe | 512 | 1,829,647 / 1,215 us | 1,490,533 / 616 us | 81.5% |
| TCP loopback | 128 | 1,344,600 / 167 us | 1,201,835 / 168 us | 89.4% |
| TCP loopback | 512 | 1,834,203 / 1,191 us | 1,431,802 / 805 us | 78.1% |

所有等价候选场景都恢复到百万级，但没有达到建议的历史 95%。保留差额主要来自 Protocol v2 有界 SendPump/Channel 竞争，以及认证调用上下文、取消/deadline、流控、遥测开关、生命周期和安全排空的必要检查。下述否决实验表明，当前不能在不损害常见场景或正确性语义的前提下安全移除这些成本。

## 宽矩阵、连接池与流式覆盖

五种传输各覆盖 payload 0/32/256/4096/65536 B 与 c1/c8/c32/c128/c256/c512。0–4096 B 的 120 个候选 stage 均零失败；单轮方向性 A/B 中，绝大多数中高并发点提升，4 KiB c128/c512 为 +2.0% 至 +18.5%。SharedMemory 256 B c128 的单轮 -3.2% 和 0 B c512 的 -1.8% 位于短样本噪声内，正式五轮 add 门槛仍分别 +10.99%/+3.69%。

64 KiB、单连接在 c128 以上会达到两版本共同的 8 MiB 发送队列上限并产生 `ResourceExhausted`；这些容量边界样本不混入吞吐门槛。AnonymousPipe 的 0.7.1 64 KiB 样本在失败后不能退出，超过四分钟后只终止该测试进程；0–4 KiB 数据完整。

1→4 连接池的 32 B c512 候选为 UDS 1.455M、Named Pipe 1.446M、SharedMemory 1.511M、TCP 1.272M QPS，全部零失败。五种传输的 Unary/C2S/S2C/Duplex、c1/c8、stream size 256 共 40 个短时 stage 全部零失败。

## 否决的优化实验

| 假设 | A/B 结果 | 决定 |
| --- | --- | --- |
| `ConcurrentQueue` + 可复用 pulse 替代 SendPump Channel | 五轮关键点 QPS -2.2%/-2.6%，尾延迟恶化，分配仅 -0.3% | 撤销；没有足够收益承担调度变化 |
| Channel `AllowSynchronousContinuations=true` | QPS 降至约 0.44M/0.71M | 撤销；生产者执行消费者延续造成严重争用 |
| 把 Server cancellation、stream dispatcher、共享内存 spill/staging 等所有池改为 Queue | S2C QPS 单轮 -2.8%，SharedMemory -2.6%，分配收益不稳定 | 只保留 Allocation Trace 证明有效的两个客户端池 |
| 禁用或改写 deadline 快路径 | profile 中单项不足 0.7%，禁用后无收益 | 保留完整 deadline 语义 |
| Throughput profile/更激进批处理 | 本机单连接 Unary flush 与尾延迟更差 | 保留 Balanced 作为正式门槛 |

## 本地正确性与稳定性门禁

- Release solution build：0 warning、0 error。
- Unit 196/196、Generator 30/30、Integration 137/137；Integration 包含取消/deadline、interceptor/telemetry、所有调用形态、连接池、生命周期、动态程序集和 collectible ALC。
- osx-arm64 独立进程 SharedMemory NativeAOT：通过，无 AOT/trimming warning。
- 0.7.2 七个 NuGet 包与干净还原 PackageSmoke：通过；SDK 包包含 Generator analyzer。
- 五传输 StreamLoadTest 的 Unary/C2S/S2C/Duplex c1/c8：40/40 stage 零失败；PR Quick 同口径 Load 与 Oneway smoke 通过。
- 120 秒 SharedMemory Chaos：893,433 success、321,939 injected、11 次滚动重启、0 unexpected，最长恢复 216 ms；最终 connections、calls、pending、streams、send queue 全为 0。
- Chaos retained memory 为 1.90 MiB→5.13 MiB，30/60/90 秒样本为 8.18/8.21/7.93 MiB。短样本包含启动、JIT 与有界池预热，只证明本次运行没有持续单调增长，不替代至少六小时的 retained-memory 百分比门槛。

最终实现没有改变 wire format，没有跳过取消、deadline、认证、流控、排空、资源上限、动态程序集租约或错误处理。跨平台结果以该版本 PR 的 Release Gate 为准。
