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
