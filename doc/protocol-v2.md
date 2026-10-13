# SharpLink Protocol v2

Protocol v2 是 SharpLink v1 的唯一线协议，不提供 Protocol v1 兼容或恢复扫描。任何 magic、长度、类型、标志或载荷结构错误都作为连接级 `ProtocolViolation` 处理并关闭连接。

当前 protocol minor 为 5，能力包含 metadata、compression、flow control、health check 和 cancellation reason。minor 5 增加请求契约身份、注册回执、握手中的服务端注册时间线身份和两向客户端流准入帧；低于 5 的 peer 在握手阶段以 `Unimplemented` 拒绝。`TimeBudget` 保留非负 duration 语义，不按旧 absolute-deadline 解释。未启用压缩、只有单方启用或 wire profile 无交集时使用未压缩连接。

## 固定帧头

所有整数使用 little-endian。固定头共 15 字节：

| Offset | 长度 | 字段 | 约束 |
|---:|---:|---|---|
| 0 | 1 | magic | 固定 `0x89` |
| 1 | 4 | payload length | 有符号 `int32`，必须在 `0..MaxFramePayloadBytes` |
| 5 | 1 | frame type | 必须是已定义的 v2 类型 |
| 6 | 1 | frame flags | 未知 bit 或类型不允许的 bit 均非法 |
| 7 | 8 | request ID | `uint64`；连接级控制帧固定为 0，成对控制请求使用非 0 correlation ID |

解析器必须先验证完整固定头与 payload length，再等待或切分 payload。半帧保留在输入缓冲；坏帧不尝试寻找下一个 magic。

## 帧类型与载荷

| 类型 | Request ID | 载荷 |
|---|---:|---|
| `HandshakeRequest` | 0 | minor、supported/required capabilities、本端 frame/window 限制、有界压缩算法列表、认证载荷 |
| `HandshakeResponse` | 0 | 协商后的 minor、capabilities、frame/window 限制、server instance / registry epoch 和唯一压缩 token；失败时为二进制错误 |
| `Ping` / `Pong` | 0 | 发送端 monotonic timestamp (`int64`) |
| `Request` | 非 0 | 下述 36 字节 identity prefix、可选 56 字节 receipt，随后是可选 TimeBudget、metadata 和业务 payload |
| `RequestAccepted` | 非 0 | 空载荷、无 flags；两向 ClientStreaming / DuplexStreaming 准入后允许启动 producer |
| `Response` | 非 0 | 成功时直接为返回 payload；`Error` 时为二进制错误 |
| `Cancel` | 非 0 | 未协商 `CancellationReason` 时为空；协商后固定一个有效 reason byte |
| `StreamData` | 非 0 | `streamId:uint16 + item payload` |
| `StreamComplete` | 非 0 | `streamId:uint16`；`Error` 时追加二进制错误 |
| `WindowUpdate` | 非 0 | `streamId:uint16 + credit:uint32`，credit 必须在 `1..int32.MaxValue` |
| `GoAway` | 0 | `lastAcceptedRequestId:uint64 + binary error/reason` |
| `HealthCheck` | 非 0 | 空；只在协商 health-check capability 后发送 |
| `HealthResponse` | 非 0 | 单字节 `Unhealthy/Ready/Draining` 状态 |

Stream ID 0 表示默认返回流，1–65535 表示显式流参数。Request ID 0 仅用于连接控制帧；溢出分配时必须跳过 0。

## 标志

- `Error`：载荷使用二进制错误格式。
- `Truncated`：错误消息已在 UTF-8 字符边界截断，只能与 `Error` 同时出现。
- `HasTimeBudget`：Request 路由前缀后包含发送瞬间剩余 RPC lifetime（`TimeSpan.Ticks`，非负 `int64`）；它是 duration，不是 UTC timestamp。
- `HasMetadata`：TimeBudget 后包含 `varuint length + metadata bytes`；metadata payload 为 `entryCount:varuint`，随后重复 UTF-8 key/value 的 `varuint length + bytes`。
- `Compressed`：对应载荷已压缩，必须先通过能力协商。
- `Cancellable`：调用允许远端取消。
- `OneWay`：单向请求，不得同时设置 `HasReturn`。
- `HasReturn`：请求期待返回 payload。

类型未列出的标志组合一律非法。`Error` 与 `Compressed` 不得同时出现；控制帧、错误帧和空业务载荷不压缩。

二进制错误的 `code:uint16` 必须映射到已定义的 `SharpLinkErrorCode`。写端与读端执行相同校验；未定义值不会被写出，也会被对端作为 `ProtocolViolation` 拒绝。

## 握手与能力

Transport（TCP 使用 TLS 时先完成 TLS）建立后，Client 首先发送 `HandshakeRequest`。Server 返回双方能力交集和较小的 frame/window 限制。当前 capability bits：

- bit 0: metadata
- bit 1: compression
- bit 2: flow control
- bit 3: protocol health check
- bit 4: cancellation reason
- bit 5: contract manifest
- bit 6: session refresh

当前 wire generation 使用 `minor 5`（`MinimumCompatibleMinorVersion == MinorVersion == 5`），以 [release version manifest](../eng/release-versions.json) 为发布边界来源。minor 只在握手/发布边界承担 grammar compatibility 校验，不参与握手后的 feature gating。协议边界“能够识别的 capability bit”与端点“实际实现并主动 advertise 的 capability”是两份独立事实：扩展 codec/rules 使其认识新 bit，不会自动让 Client/Server 宣告支持。握手成功后，普通 runtime feature code 只读取冻结的 negotiated capability set；需要具体 wire identity 的能力（例如 compression）再附带 capability-scoped profile/binding。

`HandshakeRequest` 保留既有压缩 profiles 和认证载荷编码：

```text
profileCount:uint8
repeat profileCount { profileLength:uint8 + canonical ASCII profile }
authenticationLength:varuint32 + authentication bytes
```

wire profile 最多 16 个；每个 profile 为 1–64 字节、大小写敏感的可见规范 ASCII，且列表内唯一。`HandshakeResponse` 在原有固定字段后先编码 `ServerInstanceId:16 bytes + RegistryEpoch:16 bytes`，再编码 `selectedProfileLength:uint8 + selectedProfile`。GUID 使用 .NET `Guid.TryWriteBytes` / `new Guid(span)` 的一致字节布局。实际 Client 不接受空的 server/epoch 身份，也不允许同一 Session 后续更换该身份。Server 按自身 Provider 注册顺序选择 Client 列表中的第一个匹配项；无交集时清除 compression capability 并发送零长度 profile。协商 capability 与 profile 缺失/多余或选择未被 Client 提供的 profile 都是连接级 `ProtocolViolation`。

`ISharpLinkCompressionProvider.WireProfile` 表示完整的 wire profile，不是结构化参数协商。只影响发送端成本/压缩比而不影响解码的 tuning 可以在两端不同；dictionary identity、必须支持的 window/profile、integrity mode 或其他影响解码兼容性的配置必须编码进唯一 profile，并作为不同 Provider 参与现有优先级协商。对同一 profile 配置不兼容解码参数属于 Provider 配置错误。

对端缺少任一 required capability 时，Server 返回 `Unimplemented` 错误并关闭连接。认证载荷不得超过握手/metadata 上限。

## Request 契约前缀与回执

请求业务部分之前固定包含：

| Offset | 长度 | 字段 |
| ---: | ---: | --- |
| 0 | 8 | `ContractId`，非零 |
| 8 | 8 | `MethodId` |
| 16 | 8 | `RpcAssemblyHash.High` |
| 24 | 8 | `RpcAssemblyHash.Low` |
| 32 | 1 | `RpcMethodKind` |
| 33 | 2 | `ClientStreamCount` |
| 35 | 1 | receipt mode：0 无回执，1 Compatible，2 MinimumVersion，3 ExactRegistration |

非零 mode 后追加 56 字节：`ServerInstanceId:16 + RegistryEpoch:16 + Generation:uint64 + RegistrationId:16`。
Receipt 复用固定前缀的 contract/hash，不重复编码。Hash 不可为空；GUID 不可为空，generation 可以为零。
模式、长度与 invocation shape 必须有效。此后才解析 TimeBudget、metadata 和业务 payload；压缩不覆盖 identity/receipt。

Server 在业务解码、Service 激活和 Interceptor 之前，对当前注册校验 contract/hash/receipt 并取得同一注册的 call lease。
Manifest 只提供客户端路由候选，不替代最终校验。合法旧身份得到结构化的 `Unimplemented`、`FailedPrecondition` 或 `Unavailable` 拒绝；
与已匹配 descriptor 不一致的 shape 属于协议违规。具体错误 detail 与三种版本要求见 [RPC wire identity](rpc-wire-identity.md)。

ClientStreaming / DuplexStreaming 的 producer 等待 `RequestAccepted`；Server 取得资源准入、固定注册并预留接收流之后发送该帧。
建立被拒绝、取消、超时或 Stop 时不启动 producer。ACK 不表示业务已成功执行。OneWay 不发送或等待该 ACK，其本地完成仍仅表示排队成功。

## 压缩载荷

压缩只覆盖 Generated Codec 产生的业务 payload，路由、TimeBudget、metadata 和 stream ID 始终保持未压缩，便于在分配前完成路由、资源与长度校验：

```text
Request    = route/TimeBudget/metadata envelope + originalBodyLength:uint32 + compressedBody
Response   = originalBodyLength:uint32 + compressedBody
StreamData = streamId:uint16 + originalItemLength:uint32 + compressedBody

`compressedBody` may be empty when the negotiated provider profile defines a valid zero-byte representation; Core validates the `originalLength` envelope and delegates representation validity to the provider.
```

`original*Length` 必须非零，并且与未压缩固定前缀相加后不超过协商的 frame 上限；框架在租借有界 owner 之前完成该检查。Provider 成功返回即承诺完整消费输入；Core 直接从其有界 writer 核对实际写入长度与声明的原始长度。Stream flow-control 始终按原始 item 字节计费，防止高压缩比数据绕过接收窗口。

发送端仅在业务 payload 至少 1024 B、至少节省 64 B 且节省比例不低于 5% 时选用候选压缩帧；三个阈值均可配置。候选无收益时立即归还候选 owner，原始 owner 原样交给现有 SendPump。SendPump 不识别压缩，也不会同时持有两个候选。

Core 不内置具体压缩算法，也不为算法增加私有 framing/checksum。`TryCompress` 返回 `true` 表示完整 representation 已写入，返回 `false` 只表示它无法在 Core 给定的 `maxOutputBytes` 内完成，Core 会丢弃候选并发送 raw。`Decompress` 正常返回表示完整消费 `compressedBody` 且没有忽略 trailing bytes；malformed、truncated、profile integrity failure 或超过输出上限必须确定性失败。具体 profile 的 silent-corruption 检测能力由该 profile/provider 自身定义；未来官方 profile 若要求强完整性，应使用该 wire format 自带的完整性机制，而不是把算法补偿逻辑放回 Core。

未协商却设置 `Compressed`、非法固定前缀或原始长度属于连接级 `ProtocolViolation`。已协商载荷的截断、损坏、尾部数据或输出长度不符映射为当前调用/流的 `DataLoss`；自定义 Provider 的未预期异常映射为该调用/流的安全 `Internal`。这两类调用级错误不关闭健康连接。

## 服务端主动接入控制

资源 Admission control 不增加独立 wire capability。服务端完成上述契约校验、在未创建 Service、Scope、业务 Codec 状态或执行 Interceptor 前，按 `Global → Contract → Method → Partition` 累计取得全部 permit。无等待策略、队列数量已满、队列保留字节已满、速率或并发限制拒绝时，普通调用返回 `ResourceExhausted`；Server Draining 时返回 `Unavailable`。拒绝当前调用不会关闭连接，过载解除后同一健康连接可继续调用。

等待中的 Request 保留完整 wire payload。客户端流与双向流使用 Generator 输出的 `ClientStreamCount` 预留 stream ID，并按到达顺序有界保留 `StreamData/StreamComplete`；压缩帧按实际 wire bytes 进入队列字节预算，但在保留前验证 `originalItemLength`，取得 permit 后才解压并交给 Generated Codec。队列溢出、取消、deadline、断连或 Draining 会终止整次调用并一次归还所有 owner 与 flow-control credit。

OneWay 的本地发送成功只表示帧已被客户端 SendPump 接受。服务端过载拒绝 OneWay 时不执行方法、不发送伪成功响应，并记录 dropped/resource-exhausted 指标；只有显式启用 `QueueOneWayCalls` 才允许 OneWay 等待。

## Cancel 原因与兼容

协商 `CancellationReason` capability 后，Cancel payload 固定为一个字节：

| 值 | 名称 | 语义 |
|---:|---|---|
| 0 | `Unspecified` | legacy Cancel 或未提供更具体原因 |
| 1 | `UserCancellation` | 调用方 Token 主动取消 |
| 2 | `DeadlineExceeded` | 客户端 monotonic deadline 到期 |
| 3 | `ConsumerAbandoned` | server/duplex stream 消费者提前退出 |

未协商该 capability 时，只允许 0 字节 legacy Cancel，并在服务端映射为远端主动取消。协商后空载荷、未协商却携带载荷、未知 reason 或超过一个字节都属于连接级 `ProtocolViolation`。

## 流量控制

协商 `flow control` 后，每个 `StreamData` 的原始 item payload 同时消耗所属 stream 和 connection 的发送额度；未压缩时即为 wire item 长度，压缩时取 `originalItemLength`。额度不足时 producer 异步等待；`WindowUpdate` 的 request ID 与 stream ID 精确定位原 stream，credit 同时补充两级窗口。

接收端只在消费者实际取得或明确丢弃 item 后归还 encoded byte credit，默认达到任一半窗口阈值后批量发送更新。大于 stream window 的单个 item 在不超过 `MaxFramePayloadBytes` 时允许从空窗口临时借用一次；借用未归还前继续发送即为 `ProtocolViolation`。任何 credit 加法溢出、重复归还或超过协商初始窗口也按连接级协议错误处理。

## 二进制错误

错误载荷格式为：

```text
errorCode:uint16 + messageLength:varuint32 + UTF8 message
```

错误消息默认上限 64 KiB；发送端在 UTF-8 字符边界截断并设置 `Truncated`。未知错误码、超限长度或长度不匹配均为 `ProtocolViolation`。服务实现未显式映射的异常只发送安全的 `Internal` 消息，不发送堆栈。

## 安全边界

- 默认 frame payload 上限 4 MiB，可配置范围 1 KiB–64 MiB。
- metadata 默认上限 16 KiB，错误消息默认上限 64 KiB。
- 所有网络长度在 `Slice`、复制或分配前验证。
- `HandshakeRequest/Response`、`Ping/Pong`、`Cancel`、`StreamComplete`、`WindowUpdate`、`GoAway` 和 health 帧都有类型级最小/最大载荷校验。


## Runtime response compression preference control

The current Protocol v2 handshake has a fixed client preference tuple:
`ResponseCompressionPreferenceGeneration` plus `AllowResponseCompression`. The fields are present even when the
peers ultimately have no common compression profile. A successful handshake confirms that the server has published
that initial tuple for the session.

After a session negotiates `Compression`, later client preference changes use the connection-level, uncompressed,
fixed-size `ResponseCompressionPreferenceUpdate` frame (`requestId = 0`). The server replies with
`ResponseCompressionPreferenceAck`, whose `AppliedGeneration` is cumulative: ACK `N` completes every client waiter
for generation `<= N`. A stale update never rolls server state backward. ACK is a convergence boundary for new
response-compression decisions, not a wire barrier for responses already compressed or queued.

Local Request/Response compression thresholds are not wire state. They are captured from an instance-scoped immutable
runtime send-policy snapshot at each business-frame compression decision, while provider/profile negotiation remains
build/handshake immutable.
