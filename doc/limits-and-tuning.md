# 限制与调优

先用默认值建立基线，再改一个维度。所有 Builder option 在 `Build()` 时复制和验证，之后修改原对象不会影响已构建实例。

## Protocol 默认值

| 配置 | 默认 | 约束 |
|---|---:|---|
| `MaxFramePayloadBytes` | 4 MiB | 1 KiB–64 MiB |
| `MaxMetadataBytes` | 16 KiB | 正数；也约束认证 payload |
| `MaxErrorMessageBytes` | 64 KiB | 正数 |
| `HandshakeTimeout` | 10 s | 正数，最大受 .NET timer 限制 |
| `MaxPendingRequestsPerConnection` | 65,536 | 2 次幂，最大 1,048,576 |
| `MaxConcurrentStreamsPerConnection` | 1,024 | 正数 |

双方协商 frame、flow-control 和 capability；实际连接使用双方都能接受的边界。提高 frame 上限会提高单请求最坏内存占用，不会自动提高吞吐。

## Flow control 默认值

| 配置 | 默认 |
|---|---:|
| `StreamReceiveWindowBytes` | 1 MiB |
| `ConnectionReceiveWindowBytes` | 16 MiB |
| `MaxConcurrentCallsPerConnection` | 1,024 |
| `MaxConcurrentCallsPerServer` | 65,536 |
| `MaxSendQueueBytes` | LowLatency 1 MiB / Balanced 8 MiB / Throughput 32 MiB |

Connection window 不得小于 stream window。窗口过小会增加 WindowUpdate 和等待，过大会放大每连接在途内存。Send queue 是硬字节边界，满时调用失败而不是无限增长。

`MaxConcurrentCallsPerConnection` 与 `MaxConcurrentCallsPerServer` 是相互独立的硬边界：调用必须同时取得连接槽位和服务器槽位。两者合法范围均为 `1..1,048,576`，在 `Build()` 时验证并复制；已构建的 Client/Server 不受随后修改原 option 的影响。服务器级默认值固定为 65,536，不再根据逻辑 CPU 数量变化，因此异步等待型调用可以按容量证据显式调高，同时仍保留有界保护。

提高调用上限会同时放大调用状态、请求 payload、Service scope、拦截器状态、pending request 与 send queue 的最坏内存占用。生产调优应逐级验证 `MaxPendingRequestsPerConnection`、每连接调用上限、服务器调用上限、admission 和 send queue，而不是一次性全部调到硬上限。

服务器启动时会在 `LogEvents.Server.CallCapacityConfigured` 日志中记录两个实际生效值。`sharplink.resource_exhausted` 指标保留 `rpc.side`，并通过 `rpc.sharplink.resource_exhaustion_reason` 区分低基数来源：

- `server_call_capacity`
- `per_connection_call_capacity`
- `admission_concurrency`
- `admission_queue`
- `pending_request_capacity`
- `send_queue_capacity`

wire error code 仍为 `ResourceExhausted`；稳定原因会保留在人类可读错误消息中，并由新客户端恢复到自身 metric 与 Activity tag。容量拒绝不会关闭健康连接，释放槽位后同一连接可以继续调用。

## Profile

- `LowLatency`：及时 flush、小 send queue、shared-memory 更多短 spin。
- `Balanced`：默认，适合多数服务。
- `Throughput`：更大有界 queue/ring 与批处理，允许更高尾延迟。

Profile 只提供默认值；显式配置优先。`UseRpcSessionFlush(size, latency)` 用字节阈值和最大等待共同限制 coalescing。

## 连接与 topology

- 单 endpoint pool：1–64 connections，默认 1/1。
- 静态/动态 cluster：最多 64 endpoints。
- `MaxConnectionsPerEndpoint <= MaxConnections`。
- retiring connection 有独立预算，避免 generation churn 占满 Ready budget。
- multi-cluster 默认 16 slots、总连接预算 64、并发连接 slot 4。

## Buffer 与 state store

Writer pool 默认 initial 1 KiB、最多 512 个 idle writer、最大保留 64 KiB；配置的最坏保留预算不得超过 64 MiB。大 payload writer 不回池。

State store 默认 32 stripes、每 stripe initial 8；stripe 必须是最大 1024 的 2 次幂，总 initial entry 不超过 1,048,576。提高 stripe 只在真实争用证据下进行。

## Compression

默认无 provider，即完全禁用。启用后默认阈值：payload 1024 B、至少节省 64 B、至少节省 5%。最多 16 个 profile；token 为 1–64 个可见 ASCII 字节且 case-sensitive。

## SharedMemory

每方向容量为 64 KiB–256 MiB 的 2 次幂。默认 ring：1/8/32 MiB（LowLatency/Balanced/Throughput）；默认 spin：64/8/0。生产调优必须同时观察 direct write、spill、staging、wait 和 CPU。

## Admission

排队同时受 count、bytes、delay 和 deadline 限制。partition 默认上限 1024、idle timeout 5 min。rate policy 每 scope 至多一个；多个层级可叠加。

## 性能验证

使用 [LoadTest](loadtest.md) 固定 transport、payload、connections、concurrency、duration、profile、compression 和 admission。至少交替运行基线/候选多个进程，报告 median、范围、P50/P99、allocation 和 CPU/operation；最终数字见 [性能基线](performance.md)。
