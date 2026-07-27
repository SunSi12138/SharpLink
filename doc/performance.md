# SharpLink 性能治理总表（全量）

本文档面向“当前版本较前几版性能下降”的问题，覆盖 `src/` 全链路可优化点，并给出优先级与落地顺序。

## 1. 当前回退的高概率根因（先看这里）

基于最新代码（重点是 `src/SharpLink.Client/SharpLinkClient.cs`）分析，性能回退最可能来自以下组合开销：


## 2. 全链路性能问题清单（按模块）

## 2.1 Client（`src/SharpLink.Client`）

### P0

### P1



## 2.2 Server（`src/SharpLink.Server`）

### P0


### P1

3. session 层任务创建策略

4. 握手路径字符串处理

## 2.3 Runtime（`src/SharpLink.Runtime`）

### P0


### P1




## 2.4 Generator（`src/SharpLink.Generator`）

### P0

2. blittable 跨段回退 `new byte[]`
- 位置：`src/SharpLink.Generator/RpcGenerator.cs:611-613`
- 问题：触发回退时分配临时数组。
- 优化：
  - 小尺寸 `stackalloc`，大尺寸 `ArrayPool<byte>.Shared`。

### P1

## 2.5 Transport

NamedPipe 已拆分为 client factory、server listener 与独立 connection；每条连接只创建一组 reader/writer，旧的双角色 Transport 已删除。

## Protocol v2 回归记录（2026-07-16，macOS arm64，TCP local）

命令：`SharpLink.LoadTest --mode local --transport tcp --operation add --concurrency 1,8,32 --warmup 1 --duration 3`。每档运行五次取中位数。

| 并发 | 0.4.0 早期基线 | Protocol v2（变更后） | 结论 |
|---:|---:|---:|---|
| 1 | 25.65k QPS / P99 72 μs | 26.21k QPS / P99 72 μs | 通过 |
| 8 | 168.07k QPS / P99 74 μs | 171.37k QPS / P99 69 μs | 通过 |
| 32 | 590.27k QPS / P99 81 μs | 591.08k QPS / P99 83 μs | 通过 |

相对同环境直接变更前基线，QPS 均高于 97%，P99 均低于 105%。另对 c32 使用 2 秒 warmup、5 秒测量复核五次，中位数为 584.46k QPS / P99 84 μs；该长窗数据仅作为后续 SendPump 优化参照，不与不同测量时长的门禁基线混用。

## 0.4.0 单写者 SendPump 回归（2026-07-16，同一 runner）

变更后仍使用上述 1 秒 warmup、3 秒测量命令，每档五次取中位数：

| 并发 | Protocol v2（变更前） | 字节有界 SendPump（变更后） | 结论 |
|---:|---:|---:|---|
| 1 | 26.21k QPS / P99 72 μs | 25.53k QPS / P99 73 μs | 通过（QPS 97.4%） |
| 8 | 171.37k QPS / P99 69 μs | 172.45k QPS / P99 70 μs | 通过 |
| 32 | 591.08k QPS / P99 83 μs | 605.16k QPS / P99 79 μs | 通过 |

BenchmarkDotNet `UnaryBenchmarks.Rpc_Add`（1024 invocation、3 warmup、10 iteration）结果：PayloadSize 16 为 50.84 μs / 792 B alloc，PayloadSize 256 为 53.17 μs / 792 B alloc。该结果作为 0.5.0 invoker/owner 优化的 allocation 基线；本阶段前后门禁以同命令 LoadTest 为准。

## 0.4.0 生命周期、CallOptions 与默认 deadline 回归（2026-07-16，同一 runner）

仍使用 1 秒 warmup、3 秒测量、每档五次取中位数。首次实现把每个已完成 timeout 留在优先队列中作为墓碑，超过 2,048 个取消项后反复全量压缩，导致 c32 中位数降至 11.61k QPS。修正后采用 32 路、可按 request ID 直接取消的有界堆，取消节点立即删除，Timer 只在出现更早 deadline 时重设；服务端仅为真正消费 `CancellationToken` 或输入流的方法创建每调用取消状态。

| 并发 | SendPump 基线 | 0.4.0 最终五轮中位数 | 门禁结论 |
|---:|---:|---:|---|
| 1 | 25.53k QPS / P99 73 μs | 25.61k QPS / P99 72 μs | 通过（QPS 100.3%） |
| 8 | 172.45k QPS / P99 70 μs | 170.74k QPS / P99 65 μs | 通过（QPS 99.0%） |
| 32 | 605.16k QPS / P99 79 μs | 585.48k QPS / P99 81 μs | QPS 未通过（96.75%），P99 通过（102.5%） |

c32 的 97% 阈值为 587.01k QPS，当前中位数低 1.52k QPS，即差 0.25 个百分点。该差值来自所有 Unary 默认启用 30 秒 deadline 后新增的绝对时间计算、8 字节 wire 字段和本地超时登记；不更新既有基线。进入 0.5 后首先完成 PendingRequestTable 与 timeout/deadline 仲裁整合，在继续 Invoker/Codec 重构前重新通过该门禁。

## 0.5.1 PendingRequestTable 回归（2026-07-17，同一 runner）

固定碰撞 RingBuffer 替换为 request ID 探测表；发生主槽冲突时递增 ID 寻找空槽，response dispatch 仍为 O(1)。正常路径不执行 admission semaphore/计数操作，只有真正满表才异步等待。response、error、cancel、timeout 与 disconnect 都以槽位 CAS 争夺唯一完成权，完整 64 位 request ID 必须匹配。

为避免跨日期 runner 负载变化干扰结论，在临时 detached worktree 构建 `v0.4.0`，随后与 0.5.1 使用相同机器、相同时段、1 秒 warmup、3 秒测量、每档五次取中位数：

| 并发 | v0.4.0 同时段基线 | 0.5.1 五轮中位数 | 门禁结论 |
|---:|---:|---:|---|
| 1 | 24.84k QPS / P99 74 μs | 25.21k QPS / P99 74 μs | 通过（QPS 101.5%，P99 100.0%） |
| 8 | 167.55k QPS / P99 68 μs | 167.92k QPS / P99 68 μs | 通过（QPS 100.2%，P99 100.0%） |
| 32 | 569.43k QPS / P99 83 μs | 566.75k QPS / P99 83 μs | 通过（QPS 99.5%，P99 100.0%） |

满表等待、deadline/cancellation、request ID 回绕、迟到响应与 response/cancel 竞态由 UnitTests 覆盖；本次不更新既有基线。

## 0.5.2 五类 Invoker 回归（2026-07-17，同一 runner）

Generator 不再创建捕获业务参数的 payload/stream delegate。每个方法生成静态 `RpcMethodDescriptor`、readonly request struct、缓存的 request/response Codec 和值类型 client-stream writer；`IRpcChannel` 收敛为 Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类入口。

在临时 detached worktree 构建 `3c142fe` 作为 0.5.1 基线，随后与 0.5.2 使用相同机器、相同时段、1 秒 warmup、3 秒测量、每档五次取中位数：

| 并发 | 0.5.1 同时段基线 | 0.5.2 五轮中位数 | 门禁结论 |
|---:|---:|---:|---|
| 1 | 25.68k QPS / P99 73 μs | 25.65k QPS / P99 72 μs | 通过（QPS 99.9%，P99 98.6%） |
| 8 | 170.29k QPS / P99 65 μs | 169.14k QPS / P99 65 μs | 通过（QPS 99.3%，P99 100.0%） |
| 32 | 582.69k QPS / P99 81 μs | 575.37k QPS / P99 83 μs | 通过（QPS 98.7%，P99 102.5%） |

BenchmarkDotNet `UnaryBenchmarks.Rpc_Add`（1024 invocation、3 warmup、10 iteration）A/B 结果：0.5.1 在两个 PayloadSize 参数下均为 856 B/op；0.5.2 均为 832 B/op，下降 2.8%。测量均值受本机调度抖动影响较大，因此吞吐/延迟门禁以五轮 LoadTest 中位数为准，allocation 以 MemoryDiagnoser 为准。

## 0.5.3 PooledByteBufferWriter 回归（2026-07-17，同一 runner）

Runtime 与 Generator 不再直接依赖 `ArrayBufferWriter<byte>`：Codec 顺序写入统一使用 `IBufferWriter<byte>`，协议层通过 `IRpcByteBufferWriter` 回填固定头，`OwnedFrame` 将唯一 owner 移交 SendPump，只有 flush 或 drain 后才能归还 Context 池。大于配置保留上限（硬上限 64 KiB）的数组立即归还 `ArrayPool<byte>`。

首次实现每帧都直接向共享 `ArrayPool<byte>` 租还数组，c32 中位数仅为 550.29k QPS、P99 86 μs，相对基线分别为 94.7% 和 106.2%，未通过门禁。最终实现只在 Context 的有界 writer pool 内保留不超过 64 KiB 的数组租约；直接 `Dispose()` 和超大数组仍归还 `ArrayPool<byte>`。

在临时 detached worktree 构建 `64be6ad` 作为 0.5.2 基线，随后与 0.5.3 使用相同机器、相同时段、1 秒 warmup、3 秒测量、每档五次取中位数：

| 并发 | 0.5.2 同时段基线 | 0.5.3 五轮中位数 | 门禁结论 |
|---:|---:|---:|---|
| 1 | 25.66k QPS / P99 72 μs | 25.29k QPS / P99 73 μs | 通过（QPS 98.5%，P99 101.4%） |
| 8 | 169.06k QPS / P99 66 μs | 168.75k QPS / P99 66 μs | 通过（QPS 99.8%，P99 100.0%） |
| 32 | 580.81k QPS / P99 81 μs | 573.92k QPS / P99 82 μs | 通过（QPS 98.8%，P99 101.2%） |

BenchmarkDotNet `UnaryBenchmarks.Rpc_Add` 的 PayloadSize 16/256 均为 832 B/op，与 0.5.2 持平；没有因 owner 抽象或池化 writer 增加每调用托管分配。

## 0.5.4 原生 DTO Codec Generator 回归（2026-07-17，同一 runner）

Generator 自动发现 RPC 参数、返回值、stream item 与 `[RpcSerializable]` 入口，生成字段 ID 驱动的 DTO Codec、闭合集合 Codec 和 assembly manifest。0.7.11 起第三方复杂图通过通用 manifest-scoped Codec Adapter 选择；显式 Context Codec 仍优先。生成路径不扫描程序集、不调用 `MakeGenericType`。

在临时 detached worktree 构建 `d7d20e1` 作为 0.5.3 基线，随后与 0.5.4 使用相同机器、相同时段、1 秒 warmup、3 秒测量、每档五次取中位数：

| 并发 | 0.5.3 同时段基线 | 0.5.4 五轮中位数 | 门禁结论 |
|---:|---:|---:|---|
| 1 | 26.03k QPS / P99 70 μs | 25.95k QPS / P99 70 μs | 通过（QPS 99.7%，P99 100.0%） |
| 8 | 169.88k QPS / P99 63 μs | 172.14k QPS / P99 62 μs | 通过（QPS 101.3%，P99 98.4%） |
| 32 | 589.86k QPS / P99 76 μs | 592.54k QPS / P99 76 μs | 通过（QPS 100.5%，P99 100.0%） |

BenchmarkDotNet `UnaryBenchmarks.Rpc_Add` 的 PayloadSize 16/256 仍均为 832 B/op，说明 manifest 快照与生成 Codec 优先级没有增加基础 Unary 每调用分配。

0.7.11 的 SharpPack Adapter、原生热路径和五轮 TCP QPS/P99 结果见 [`performance-0.7.11.md`](performance-0.7.11.md)。

0.8.0 第一批深度审核的同机运行时热路径对照见 [`performance-0.8.0.md`](performance-0.8.0.md)。

0.8.1 的三轮交替 `List<T>` RPC 分配与吞吐门禁见 [`performance-0.8.1.md`](performance-0.8.1.md)。

0.8.2 的三启动 frame parser 控制/候选门禁见 [`performance-0.8.2.md`](performance-0.8.2.md)。

0.8.3 的三启动 metadata 构造/解码分配门禁见 [`performance-0.8.3.md`](performance-0.8.3.md)。

0.8.24 的大规模 Generator timeout/union 分析门禁见 [`performance-0.8.24.md`](performance-0.8.24.md)。

0.8.25 的契约表面与生成标识 Generator 门禁见 [`performance-0.8.25.md`](performance-0.8.25.md)。

0.8.26 的 Oneway、DTO、字典与契约成员 Generator 门禁见 [`performance-0.8.26.md`](performance-0.8.26.md)。

0.8.27 的响应、stream token 与 writer pool 运行时门禁见 [`performance-0.8.27.md`](performance-0.8.27.md)。

0.8.28 的配置边界、错误帧写出与交替运行时 A/B 门禁见 [`performance-0.8.28.md`](performance-0.8.28.md)。

0.8.29 的 pending disposal、单调心跳与多集群状态读取 A/B 门禁见 [`performance-0.8.29.md`](performance-0.8.29.md)。

0.8.30 的 Generator 任务形状与本地健康探测分配门禁见 [`performance-0.8.30.md`](performance-0.8.30.md)。

0.8.31 的原始 frame writer 同时段基线与 API 边界门禁见 [`performance-0.8.31.md`](performance-0.8.31.md)。

0.8.32 的 server admission 延迟/分配对照与被拒池化方案见 [`performance-0.8.32.md`](performance-0.8.32.md)。

0.8.33 的 400 枚举方法 Generator 压力门禁见 [`performance-0.8.33.md`](performance-0.8.33.md)。

## 0.5.5 Stream/Connection 字节流控回归（2026-07-17，同一 runner）

Protocol v2 握手现在协商 `FlowControl` capability。每个 stream 与 connection 同时维护有界 byte credit；正常发送快路径无 Task 分配，额度耗尽时才创建 FIFO waiter。dispatcher 在消费者实际取得 item 后按 encoded byte count 归还额度，取消、deadline 和 session fault 会完成全部等待者。一字节窗口的真实 client/server stream Integration 已验证暂停与恢复，单个合法大 item 使用一次临时借用。

以 `cbcacc1` 作为 0.5.4 基线，使用 1 秒 warmup、3 秒测量、每档五次取中位数：

| 并发 | 0.5.4 同时段基线 | 0.5.5 五轮中位数 | 门禁结论 |
|---:|---:|---:|---|
| 1 | 25.59k QPS / P99 71 μs | 25.45k QPS / P99 71 μs | 通过（QPS 99.4%，P99 100.0%） |
| 8 | 171.14k QPS / P99 66 μs | 170.20k QPS / P99 70 μs | 短窗 P99 边界抖动，长窗复核通过 |
| 32 | 595.33k QPS / P99 76 μs | 595.35k QPS / P99 76 μs | 通过（QPS 100.0%，P99 100.0%） |

c8 另以 2 秒 warmup、5 秒测量执行五组严格交替 A/B：基线中位数 174.59k QPS / P99 62 μs，0.5.5 为 172.89k QPS / P99 63 μs，分别为 99.0% 与 101.6%，通过门禁。BenchmarkDotNet `UnaryBenchmarks.Rpc_Add` 的 PayloadSize 16/256 均保持 832 B/op。

## 0.5.6 有界连接池回归（2026-07-17，同一 runner）

每个 Client endpoint 现在持有不可变的 `MinConnections/MaxConnections` 快照。默认 1/1 路径直接使用唯一 ready session；多连接才执行 power-of-two choices。请求与 stream 固定绑定所选 session，`GoAway` session 从选择快照移除并在 active request 归零后释放。压力扩容由单一合并 worker 执行。

以 `6b77684` 作为 0.5.5 基线，使用 1 秒 warmup、3 秒测量，严格交替 A/B，每档五次取中位数：

| 并发 | 0.5.5 同时段基线 | 0.5.6 默认 1/1 | 门禁结论 |
|---:|---:|---:|---|
| 1 | 25.59k QPS / P99 70 μs | 25.52k QPS / P99 71 μs | 通过（QPS 99.7%，P99 101.4%） |
| 8 | 170.02k QPS / P99 68 μs | 171.13k QPS / P99 64 μs | 通过（QPS 100.7%，P99 94.1%） |
| 32 | 593.26k QPS / P99 76 μs | 592.90k QPS / P99 76 μs | 通过（QPS 99.9%，P99 100.0%） |

BenchmarkDotNet `UnaryBenchmarks.Rpc_Add` 的 PayloadSize 16/256 仍均为 832 B/op。额外的本机 TCP c128 诊断中，1/1 为 1.248M QPS / P99 314 μs，1/2 为 1.086M QPS / P99 258 μs：第二条本机连接降低尾延迟但增加调度成本，因此默认仍为 1/1；是否扩池应按真实跨进程网络与 payload 矩阵验证，不把单次本机吞吐结果写成通用收益承诺。

## 0.6.3 Interceptor 空管线回归（2026-07-17，同一 runner）

Client/Server interceptor 只在 Builder 显式注册后构建调用上下文和 delegate 链。首次实现为所有服务调用创建完整 Server context，使 `UnaryBenchmarks.Rpc_Add` 从 832 B/op 增至 944 B/op，未通过 allocation 门禁；最终改为默认路径继续使用轻量 ambient context，完整 context 仅在启用 interceptor 或映射异常时创建。

以 `f33e40b`（0.6.2）作为基线，使用 1 秒 warmup、3 秒测量，严格交替 A/B，每档五次取中位数：

| 并发 | 0.6.2 同时段基线 | 0.6.3 空管线 | 门禁结论 |
|---:|---:|---:|---|
| 1 | 25.63k QPS / P99 71 μs | 26.16k QPS / P99 70 μs | 通过（QPS 102.1%，P99 98.6%） |
| 8 | 171.08k QPS / P99 62 μs | 171.18k QPS / P99 62 μs | 通过（QPS 100.1%，P99 100.0%） |
| 32 | 591.24k QPS / P99 76 μs | 589.23k QPS / P99 76 μs | 通过（QPS 99.7%，P99 100.0%） |

BenchmarkDotNet `UnaryBenchmarks.Rpc_Add` 的 PayloadSize 16/256 在基线与最终实现中均为 832 B/op。启用 interceptor 的路径有意为可变上下文、短路和 pipeline 支付额外成本，不计入默认空管线门禁。

## 0.6.4 OpenTelemetry 空监听路径回归（2026-07-17，同一 runner）

Activity 与调用级 Meter 只在对应 listener 启用时构造调用 observer、tag collection 和计时状态；连接、队列、pending、stream 与字节指标在 instrument 未启用时直接返回。默认应用未接入 OpenTelemetry 时继续走原有直接调用路径。

以 `fc0a5cc`（0.6.3）作为基线，使用 1 秒 warmup、3 秒测量，严格交替 A/B，每档五次取中位数：

| 并发 | 0.6.3 同时段基线 | 0.6.4 无 listener | 门禁结论 |
|---:|---:|---:|---|
| 1 | 25.95k QPS / P99 71 μs | 25.93k QPS / P99 71 μs | 通过（QPS 99.9%，P99 100.0%） |
| 8 | 170.32k QPS / P99 63 μs | 170.08k QPS / P99 62 μs | 通过（QPS 99.9%，P99 98.4%） |
| 32 | 591.71k QPS / P99 76 μs | 587.94k QPS / P99 76 μs | 通过（QPS 99.4%，P99 100.0%） |

BenchmarkDotNet `UnaryBenchmarks.Rpc_Add` 的 PayloadSize 16/256 均保持 832 B/op；启用 listener 后的 Activity/tag 成本属于显式可观测性成本，不计入默认热路径门禁。

## 0.6.5 DI/健康检查默认 Singleton 回归（2026-07-17，同一 runner）

服务表改为生命周期 registration，但默认 Singleton 在首次激活后只执行缓存实例读取；只有用户显式选择 Scoped/Transient 才创建调用 scope。健康检查使用独立控制帧和共享有界 pending table，不进入普通业务调用路径。

以 `07f9661`（0.6.4）作为基线，使用 1 秒 warmup、3 秒测量，严格交替 A/B，每档五次取中位数：

| 并发 | 0.6.4 同时段基线 | 0.6.5 默认 Singleton | 门禁结论 |
|---:|---:|---:|---|
| 1 | 25.06k QPS / P99 72 μs | 26.02k QPS / P99 71 μs | 通过（QPS 103.8%，P99 98.6%） |
| 8 | 170.96k QPS / P99 62 μs | 170.76k QPS / P99 62 μs | 通过（QPS 99.9%，P99 100.0%） |
| 32 | 587.77k QPS / P99 77 μs | 586.62k QPS / P99 76 μs | 通过（QPS 99.8%，P99 98.7%） |

BenchmarkDotNet `UnaryBenchmarks.Rpc_Add` 的 PayloadSize 16/256 均为 832 B/op。Scoped/Transient 的 scope 与释放成本属于显式生命周期语义，不影响默认门禁。
