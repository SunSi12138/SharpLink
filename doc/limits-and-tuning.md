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
| `MaxPreCreditSerializedBytes` | 4 MiB |
| `MaxConcurrentCallsPerConnection` | 1,024 |
| `MaxConcurrentCallsPerServer` | 65,536 |
| `MaxSendQueueBytes` | LowLatency 1 MiB / Balanced 8 MiB / Throughput 32 MiB |

`ConnectionReceiveWindowBytes` 是 protocol/wire flow-control credit；`MaxPreCreditSerializedBytes` 是**独立的本地 process-memory admission**，只保护无法在序列化前得到 exact encoded size 的 streaming fallback。它不会写入 handshake、不会修改 peer-visible receive window，也不会随 configured/negotiated connection window 自动变化。

`MaxPreCreditSerializedBytes` 的精确定义是 **byte-owner/admission budget**，不是所有长期存活 serialized writer 的 aggregate cap。一个已经进入有界 FIFO 的 budget waiter 本身已经持有完整 serialized writer，因此 waiter backing 需要单独计入总内存 envelope。令 `B = MaxPreCreditSerializedBytes`、`F = negotiated max-frame payload`、`S = max concurrent streams`，当前 waiter 上限为：

```text
W = min(S, max(1, floor(B / F)))
```

在合法 frame-size 约束下，owner payload 最多为 `max(B, F)`（`B < F` 时允许一个合法 oversized item 作为 sole owner），queued waiter payload 最多为 `W * F`。因此该 subsystem 的长期 serialized **payload** 硬上界是：

```text
aggregateSerializedPayload <= max(B, F) + W * F
```

这个公式不包含 frame/header、buffer-pool capacity rounding 等额外开销。默认 `B = F = 4 MiB` 且 `W = 1`，所以默认 aggregate serialized-payload envelope 最多约 **8 MiB**，而不是 4 MiB。相同大小的 64 KiB / 1 MiB starvation 表通常明显低于该最坏混合大小上界；混合大小回归测试覆盖了 owners 填满 4 MiB budget、同时保留一个接近 max-frame 的 serialized waiter 的情况。

`MaxPreCreditSerializedBytes` 默认固定为 4 MiB。这个值等于默认 `MaxFramePayloadBytes`：在默认 protocol 配置下，一个最大合法 unsized item 可以正常占用 owner budget，而 starved receiver 又不能把 owner/admission bytes 放大到默认 16 MiB connection window。Phase 0 的 7950X starved-memory A/B 显示 128 × 1 MiB unsized streams 在有界 admission 下 retained working-set/private-memory 可大幅下降；同时 immediate-credit fast path 不进入该预算，因此没有必要用更大的 wire window 作为本地内存默认值。该默认值也不随 performance profile 隐式变化。

显式调优时，把这两个资源分开考虑：

- 要改变网络在途/peer-visible credit，调 `ConnectionReceiveWindowBytes`；
- 要改变本地“已序列化、正在等 credit”的 byte-owner/admission budget，调 `MaxPreCreditSerializedBytes`，并按上面的 aggregate 公式同时评估 bounded waiter backing。

本地 budget 可以小于或大于 connection window。小于合法 max-frame payload 时，单个合法 oversized item 仍允许作为 sole owner 临时借用预算，避免永久等待；同时 waiter 数由 configured budget、negotiated max-frame payload 和 concurrent-stream limit 内部推导并保持有界。不要为了放宽本地 pre-credit memory admission 去扩大 wire receive window，也不要为了收紧 wire flow control 被迫压低本地 budget。

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

wire error code 仍为 `ResourceExhausted`；一个单字节有界 discriminator 位于可截断文本之前，新客户端据此恢复自身 metric 与 Activity tag，同时仍兼容识别旧消息中的稳定原因。容量拒绝不会关闭健康连接，释放槽位后同一连接可以继续调用。

## Session 配置生命周期与运行时更新

**Dynamic configuration 不等于 active-session renegotiation。** 当前 Session/wire-sensitive 配置在构建或 handshake 时进入一个稳定 owner；已经建立的 Session 不会因为控制面更新而逐字段切换协议、容量或身份状态。

| 配置 | owner / capture 点 | effective lifetime | runtime update | 对现有连接 / Session | 对未来连接 / Session |
|---|---|---|---|---|---|
| Protocol / frame limits | frozen RuntimeContext；handshake 后为 `NegotiatedSessionOptions` | Session | 当前无通用 desired-session publication；语义为 **new-session only** | 不变，不做 active renegotiation | 若未来加入 publication，只允许新 Session 捕获新 generation |
| Negotiated flow-control window limits | handshake -> `NegotiatedSessionOptions` / `StreamFlowController` | Session | **new-session only** | configured/negotiated window 不变；正常 credit consume/return/`WINDOW_UPDATE` 继续演化 | 新 Session 才可使用不同初始/协商窗口 |
| `MaxPendingRequestsPerConnection` | `ClientConnection` 构造 `PendingRequestTable` | physical Client connection | construction only | 不 live-resize active table | 新 physical connection 使用其构造快照 |
| `MaxConcurrentStreamsPerConnection` | `RpcSession` / `StreamManager` / flow-controller construction | Session | **new-session only** | 不 live-resize | 新 Session 使用新结构容量（若未来有 publication） |
| Compression Provider / `WireProfile` | frozen provider bindings；handshake -> negotiated `CompressionBinding` | Session | build / **new-session only** | negotiated binding 不变 | 新 Session 可协商新的 provider/profile set（若未来有 publication） |
| Authentication / handshake-sensitive identity | authenticator at handshake；成功 identity 存入 connection state | Connection / Session | **new-session only** | 不替换 established identity/security context | 新 handshake 可获得新的 credential/context |
| `MaxConcurrentConnections` | `ServerConnectionAdmission` stable counter/lease domain + immutable target pair | Server admission lifetime | `ISharpLinkServer.UpdateConnectionAdmission(...)` | shrink 不 force-close 已 admitted connection | 后续 Accept acquisition 立即按新 target 判定 |
| `MaxConcurrentHandshakes` | 同一个 `ServerConnectionAdmission` handshake counter/lease domain | Server admission lifetime | `ISharpLinkServer.UpdateConnectionAdmission(...)` | shrink 不 cancel 已运行 handshake | 后续 handshake acquisition 立即按新 target 判定 |

“New-session only”描述的是**正确生命周期边界**，不是声称当前已经存在通用 Session 配置热更新 API。当前大部分这些设置实际仍是 build-only/frozen composition；如果以后需要 running Client/Server 发布 desired Session configuration，必须一次发布并在一次 physical connection/session creation 开始时捕获一个完整 immutable generation，不能把 `SharpLinkRuntimeContext` 改成逐字段可变对象。

连接 admission 是例外，因为它位于 Session Ready 之前。`UpdateConnectionAdmission(...)` 每次构造并验证一份完整的 `SharpLinkConnectionAdmissionOptions` candidate，再原子发布 connection/handshake target pair；现有 `ServerConnectionAdmission` counters 与 leases 不会被替换。提高 target 会给未来 acquisition 增加容量；降低 target 只阻止新的非法 acquisition，直到自然 cleanup 使当前 usage 低于新 target。

每次 runtime update 都按 `SharpLinkConnectionAdmissionOptions` 的安全默认和 #250 语义构造**完整 desired pair**：未显式设置 handshake bound 时仍使用 64 并在 connection bound 更低时 clamp；显式 `MaxConcurrentHandshakes = 0` 仍表示没有独立 handshake bound（effective bound 跟随 connection bound）。如果要保留一个非默认 handshake target，update callback 中应同时重新指定它。

本地 compression send threshold/allow policy 是另一个 next-message runtime subsystem，不会替换 negotiated Provider/`WireProfile`。同样，flow-control 的 credit consume/return/`WINDOW_UPDATE` 是 active protocol state evolution，不是 negotiated window configuration 热更新。

## Profile

- `LowLatency`：及时 flush、小 send queue、shared-memory 更多短 spin。
- `Balanced`：默认，适合多数服务。
- `Throughput`：更大有界 queue/ring 与批处理，允许更高尾延迟。

Profile 为相关资源提供默认值；显式配置优先。`MaxPreCreditSerializedBytes` 的 4 MiB 默认是独立本地 memory policy，不由 profile 或 wire window 派生。`UseRpcSessionFlush(size, latency)` 用字节阈值和最大等待共同限制 coalescing。

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
