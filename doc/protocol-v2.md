# SharpLink Protocol v2

Protocol v2 是 SharpLink 的唯一线协议，不提供 Protocol v1 兼容或恢复扫描。任何 magic、长度、类型、标志或载荷结构错误都作为连接级 `ProtocolViolation` 处理并关闭连接。

当前 protocol minor 与兼容性下限均为 5。minor 5 引入必需的逐调用 contract admission 与带注册身份的 manifest；低于 5 的 peer 在握手阶段以 `Unimplemented` 拒绝，不会尝试按新布局解码旧载荷。历史上的 minor 4 已将 absolute deadline 改为 `TimeBudget` duration，该语义在 minor 5 中保持不变。未启用压缩、只有单方启用或 wire profile 无交集时使用未压缩连接。

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
| `HandshakeResponse` | 0 | 协商后的 minor、capabilities、frame/window 限制和唯一压缩 token；失败时为二进制错误 |
| `Ping` / `Pong` | 0 | 发送端 monotonic timestamp (`int64`) |
| `Request` | 非 0 | `contractId:int64 + methodId:int64`，随后是必需的 v1 contract admission 前缀、可选 TimeBudget、metadata 和业务 payload |
| `Response` | 非 0 | 成功时直接为返回 payload；`Error` 时为二进制错误 |
| `Cancel` | 非 0 | 未协商 `CancellationReason` 时为空；协商后固定一个有效 reason byte |
| `StreamData` | 非 0 | `streamId:uint16 + item payload` |
| `StreamComplete` | 非 0 | `streamId:uint16`；`Error` 时追加二进制错误 |
| `WindowUpdate` | 非 0 | `streamId:uint16 + credit:uint32`，credit 必须在 `1..int32.MaxValue` |
| `GoAway` | 0 | `lastAcceptedRequestId:uint64 + binary error/reason` |
| `HealthCheck` | 非 0 | 空；只在协商 health-check capability 后发送 |
| `HealthResponse` | 非 0 | 单字节 `Unhealthy/Ready/Draining` 状态 |
| `ContractManifest` (13) | 0 | v1 格式版本、server/epoch、registry generation 与有序 contract/hash/registration entries |

Stream ID 0 表示默认返回流，1–65535 表示显式流参数。Request ID 0 仅用于连接控制帧；溢出分配时必须跳过 0。

## 标志

- `Error`：载荷使用二进制错误格式。
- `Truncated`（bit 1，值 `0x02`）：在允许错误的非 Request 帧中表示消息已在 UTF-8 字符边界截断，只能与 `Error` 同时出现。
- `HasContractIdentity`（同为 bit 1，值 `0x02`）：仅在 Request 中表示必需的 v1 contract admission 前缀。这是按帧类型解释的别名，不表示 Request 被截断；Request 不允许 `Error`。
- `HasTimeBudget`：Request 的完整 contract admission 前缀后包含发送瞬间剩余 RPC lifetime（`TimeSpan.Ticks`，非负 `int64`）；它是 duration，不是 UTC timestamp。
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
- bit 7 (`1UL << 7`，值 `128` / `0x80`): contract admission

当前 3.0 wire generation 使用 `minor 5`（`MinimumCompatibleMinorVersion == MinorVersion == 5`），相对实际已发布的 2.0.3/minor 4 增加一次。开发期使用过的中间编号不构成发布兼容性边界。minor 只在握手/发布边界承担 grammar compatibility 校验，不参与握手后的 feature gating。协议边界“能够识别的 capability bit”与端点“实际实现并主动 advertise 的 capability”是两份独立事实：扩展 codec/rules 使其认识新 bit，不会自动让 Client/Server 宣告支持。握手成功后，普通 runtime feature code 只读取冻结的 negotiated capability set；需要具体 wire identity 的能力（例如 compression）再附带 capability-scoped profile/binding。

当前 `HandshakeRequest` 在三个固定限制字段后编码（保留原有 response-compression preference tuple）：

```text
responseCompressionPreferenceGeneration:uint64
allowResponseCompression:uint8  // 0 or 1
profileCount:uint8
repeat profileCount { profileLength:uint8 + canonical ASCII profile }
authenticationLength:varuint32 + authentication bytes
```

wire profile 最多 16 个；每个 profile 为 1–64 字节、大小写敏感的可见规范 ASCII，且列表内唯一。`HandshakeResponse` 在固定字段后编码 `selectedProfileLength:uint8 + selectedProfile`。Server 按自身 Provider 注册顺序选择 Client 列表中的第一个匹配项；无交集时清除 compression capability 并发送零长度 profile。协商 capability 与 profile 缺失/多余或选择未被 Client 提供的 profile 都是连接级 `ProtocolViolation`。

`ISharpLinkCompressionProvider.WireProfile` 表示完整的 wire profile，不是结构化参数协商。只影响发送端成本/压缩比而不影响解码的 tuning 可以在两端不同；dictionary identity、必须支持的 window/profile、integrity mode 或其他影响解码兼容性的配置必须编码进唯一 profile，并作为不同 Provider 参与现有优先级协商。对同一 profile 配置不兼容解码参数属于 Provider 配置错误。

对端缺少 Client 声明的任一 required capability 时，Server 返回 `Unimplemented` 错误并关闭连接。生产 Client 将 `ContractManifest | ContractAdmission` 都列为 required；生产 Server 也独立要求成功协商 `ContractAdmission`，即使当前 minor 的 Client 没有把它列为 required，缺失时仍以握手 `ProtocolViolation` 拒绝。认证载荷不得超过握手/metadata 上限。

## Request contract admission v1

每个生产 Request 都必须设置 `HasContractIdentity`，并且连接已经协商 `ContractAdmission`。路由之后的布局如下；offset 均相对 Request payload 起点：

| Offset | 长度 | 字段 |
|---:|---:|---|
| 0 | 8 | `contractId:int64` |
| 8 | 8 | `methodId:int64` |
| 16 | 8 | `rpcAssemblyHash.High:uint64` |
| 24 | 8 | `rpcAssemblyHash.Low:uint64` |
| 32 | 1 | admission format version，固定 `1` |
| 33 | 1 | `match:uint8`：`0=Compatible`、`1=MinimumVersion`、`2=ExactRegistration` |
| 34 | 16 | 仅 match 1/2：`serverInstanceId:Guid` |
| 50 | 16 | 仅 match 1/2：`registryEpoch:Guid` |
| 66 | 8 | 仅 match 1/2：`generation:int64`，非负 |
| 74 | 16 | 仅 match 1/2：`registrationId:Guid` |

Compatible 的 routing + admission 共 34 字节；MinimumVersion / ExactRegistration 共 90 字节。之后依次是 `HasTimeBudget` 控制的 8 字节 time budget、`HasMetadata` 控制的有界 metadata，再之后才是业务参数。`RpcAssemblyHash` 的 High/Low 两字均按 little-endian 编码，且整个 hash 不得为空。

Guid 使用 .NET `Guid.TryWriteBytes(Span<byte>)` / `new Guid(ReadOnlySpan<byte>)` 的默认 16 字节布局，不是 RFC UUID network byte order。例如 `00112233-4455-6677-8899-aabbccddeeff` 编码为 `33 22 11 00 55 44 77 66 88 99 aa bb cc dd ee ff`。本文所有 Guid 字段使用同一布局。

MinimumVersion 要求相同的非空 server/epoch，以及当前 registry generation 不小于请求的 generation；其 registrationId 字段不参与比较，可以为空。ExactRegistration 还要求非空 registrationId 与选中的实现注册完全一致。两个模式都同时要求精确的 contract/hash 相等；不同 server/epoch 的 generation 不可比较。

缺失必需标志、未协商身份能力、空 hash、未知 format version/match、截断或非法 constraint 属于连接级 `ProtocolViolation`。合法但不匹配当前注册的普通调用返回 `FailedPrecondition`，不创建 service/scope，不执行 interceptor，不解压业务参数或运行 Generated Codec。无法确认请求流形状的 OneWay 被关闭连接而不是尝试解码；已经预留了已知流路由的排队调用按其已知路由清理。

对于带版本约束的 Request，Server 在一次 registry 边界内检查版本和选中的 registration，并将该 registration 及其 codec 绑定携带到后续分派。并发 Replace 不会在检查旧注册后改用新 codec；动态 module 的引用取得仍是进入用户代码的最终边界，已经开始退休的注册不能仅凭身份匹配绕过该检查。StreamData 不重复携带 identity，已接纳的流继续使用原 Request 固定的会话/注册。

## ContractManifest v1

`ContractManifest` 是 Request ID 为 0 的未压缩控制帧。固定 header 为 45 字节：

| Offset | 长度 | 字段 |
|---:|---:|---|
| 0 | 1 | manifest format version，固定 `1` |
| 1 | 16 | `serverInstanceId:Guid` |
| 17 | 16 | `registryEpoch:Guid` |
| 33 | 8 | `generation:int64`，非负 |
| 41 | 4 | `entryCount:int32`，非负 |

随后每条 entry 固定 40 字节：`contractId:int64 + rpcAssemblyHash.High:uint64 + rpcAssemblyHash.Low:uint64 + registrationId:Guid`。contract ID 必须非零、唯一并按有符号 int64 严格递增；hash 必须非空。载荷长度必须恰为 `45 + entryCount * 40`，在任何按 entry 数量分配之前检查完整长度和协商的 frame 上限。

生产 Server 发布非空且实例独立的 server/epoch，所有 callable entries 都有非空 registrationId。读取器只允许 server/epoch 同时存在或同时为空；同时为空的 compatibility-only snapshot 永远不能满足 versioned requirement。Register/Replace 返回的不可变标量 receipt 与该提交的 server/epoch/generation/hash/registrationId 对齐。只注册 codec、没有导出 callable service 的模块返回空 receipt 集合。Manifest 传播可以晚于提交，因此它是客户端观测；实际接纳仍由 Server 对当前选中注册的检查决定。

## 压缩载荷

压缩只覆盖 Generated Codec 产生的业务 payload，路由、完整的可变长度 contract admission 前缀、TimeBudget、metadata 和 stream ID 始终保持未压缩，便于在分配前完成路由、资源与长度校验：

```text
Request    = route/contract-admission/TimeBudget/metadata envelope + originalBodyLength:uint32 + compressedBody
Response   = originalBodyLength:uint32 + compressedBody
StreamData = streamId:uint16 + originalItemLength:uint32 + compressedBody

`compressedBody` may be empty when the negotiated provider profile defines a valid zero-byte representation; Core validates the `originalLength` envelope and delegates representation validity to the provider.
```

`original*Length` 必须非零，并且与完整未压缩前缀（包括可选的 registration constraint 和 metadata）相加后不超过协商的 frame 上限；框架在租借有界 owner 之前完成该检查。Provider 成功返回即承诺完整消费输入；Core 直接从其有界 writer 核对实际写入长度与声明的原始长度。Stream flow-control 始终按原始 item 字节计费，防止高压缩比数据绕过接收窗口。

发送端仅在业务 payload 至少 1024 B、至少节省 64 B 且节省比例不低于 5% 时选用候选压缩帧；三个阈值均可配置。候选无收益时立即归还候选 owner，原始 owner 原样交给现有 SendPump。SendPump 不识别压缩，也不会同时持有两个候选。

Core 不内置具体压缩算法，也不为算法增加私有 framing/checksum。`TryCompress` 返回 `true` 表示完整 representation 已写入，返回 `false` 只表示它无法在 Core 给定的 `maxOutputBytes` 内完成，Core 会丢弃候选并发送 raw。`Decompress` 正常返回表示完整消费 `compressedBody` 且没有忽略 trailing bytes；malformed、truncated、profile integrity failure 或超过输出上限必须确定性失败。具体 profile 的 silent-corruption 检测能力由该 profile/provider 自身定义；未来官方 profile 若要求强完整性，应使用该 wire format 自带的完整性机制，而不是把算法补偿逻辑放回 Core。

未协商却设置 `Compressed`、非法固定前缀或原始长度属于连接级 `ProtocolViolation`。已协商载荷的截断、损坏、尾部数据或输出长度不符映射为当前调用/流的 `DataLoss`；自定义 Provider 的未预期异常映射为该调用/流的安全 `Internal`。这两类调用级错误不关闭健康连接。

## 服务端主动接入控制

这里的并发/速率/队列 admission control 不增加额外 wire capability，也不改变上述客户端发送格式；它与必需的 `ContractAdmission` 身份检查是不同的控制。服务端在未创建 Service、Scope、Codec 状态或执行 Interceptor 前，按 `Global → Contract → Method → Partition` 累计取得全部 permit。无等待策略、队列数量已满、队列保留字节已满、速率或并发限制拒绝时，普通调用返回 `ResourceExhausted`；Server Draining 时返回 `Unavailable`。拒绝当前调用不会关闭连接，过载解除后同一健康连接可继续调用。

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
errorCode:uint16 + detailCode:uint16 + messageLength:varuint32 + UTF8 message
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
