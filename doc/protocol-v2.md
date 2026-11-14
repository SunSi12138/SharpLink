# SharpLink Protocol v2

Protocol v2 是当前 SharpLink 的唯一线协议，不提供 Protocol v1 兼容或恢复扫描。任何 magic、长度、类型、标志或载荷结构错误都作为连接级 `ProtocolViolation` 处理并关闭连接。

当前 protocol minor 和最低兼容 minor 均为 **5**。它在已发布 minor 4 的 TimeBudget/error grammar
之上增加 Session-local contract binding、逐调用流准入和 OneWay 输入有界形态；minor 4 peer 在
握手阶段以 `Unimplemented` 拒绝，不能把旧 ContractId 请求误解释成 BindingId。
metadata、compression、flow control、health check、cancellation reason、contract manifest 和
session refresh 仍按各自协商能力工作；无共同压缩 profile 时使用未压缩连接。

3.0 的进程内 Generated ABI 为 **API 6**，与 wire minor 是独立版本轴。当前包双进程门禁验证
同一批 3.0 包，不提供 2.0.3 ↔ 3.0 的跨 major 互操作承诺。

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
| `Request` | 非 0 | `bindingId:uint64 + methodId:int64`（16 B），随后是可选 TimeBudget、metadata 和业务 payload |
| `Response` | 非 0 | 成功时直接为返回 payload；`Error` 时为二进制错误 |
| `Cancel` | 非 0 | 未协商 `CancellationReason` 时为空；协商后固定一个有效 reason byte |
| `StreamData` | 非 0 | `streamId:uint16 + item payload` |
| `StreamComplete` | 非 0 | `streamId:uint16`；`Error` 时追加二进制错误 |
| `WindowUpdate` | 非 0 | `streamId:uint16 + credit:uint32`，credit 必须在 `1..int32.MaxValue` |
| `GoAway` | 0 | `lastAcceptedRequestId:uint64 + binary error/reason` |
| `HealthCheck` | 非 0 | 空；只在协商 health-check capability 后发送 |
| `HealthResponse` | 非 0 | 单字节 `Unhealthy/Ready/Draining` 状态 |
| `ContractManifest` (13) | 0 | generation、count、Server/epoch、按 ContractId 排序的 ID/hash 条目 |
| `ResponseCompressionPreferenceUpdate` / `Ack` (14/15) | 0 | 固定大小的 preference / cumulative applied generation |
| `SessionRefreshRequested` (16) | 0 | server instance 与 desired generation |
| `BindContract` (17) | 非 0 operation ID | 40 B identity/constraint，或含 receipt 的 112 B |
| `BindAck` (18) | 对应 operation ID | 40 B status、contract、binding token、registration、publication generation |
| `BindingInvalidated` (19) | 0 | `count:int64 + count × token:uint64`；count 为 1–64 |
| `BindingRelease` (20) | 0 | 一个完整 `token:uint64` |
| `RequestAccepted` (21) | 对应调用 ID | 空载荷，无 flags；不是 BindAck 或业务结果 |
| `OneWayInputRequest` (22) | 非 0 | `streamCount:uint8`（1–127）+ 普通 Request envelope |

Stream ID 0 表示默认返回流，1–65535 表示显式流参数。普通 Request、Response、Cancel 和 Stream 帧使用同一非零调用 ID；Bind/Ack 使用独立有界 operation correlation。

## 标志

- `Error`：载荷使用二进制错误格式。
- `Truncated`：错误消息已在 UTF-8 字符边界截断，只能与 `Error` 同时出现。
- `HasTimeBudget`：Request 路由前缀后包含构造请求帧时采样的剩余 RPC lifetime（`TimeSpan.Ticks`，非负 `int64`）；它是 duration，不是 UTC timestamp。
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

当前 wire generation 使用 `minor 5`（`MinimumCompatibleMinorVersion == MinorVersion == 5`）。
minor 在握手/发布边界验证 grammar；握手后能力判断读取冻结的 negotiated capability set，
不按 minor 重新猜测能力。能够识别某个 bit 不等于端点已实现并主动 advertise 该能力。
压缩等能力还需匹配其 capability-scoped profile/binding。

minor 5 的 `HandshakeRequest` 保留三个固定 frame/window 限制字段后的压缩协商部分：

```text
responseCompressionPreferenceGeneration:uint64
allowResponseCompression:uint8  // 0 or 1
profileCount:uint8
repeat profileCount { profileLength:uint8 + canonical ASCII profile }
authenticationLength:varuint32 + authentication bytes
```

wire profile 最多 16 个；每个 profile 为 1–64 字节、大小写敏感的可见规范 ASCII，且列表内唯一。`HandshakeResponse` 在固定字段后编码 `selectedProfileLength:uint8 + selectedProfile`。Server 按自身 Provider 注册顺序选择 Client 列表中的第一个匹配项；无交集时清除 compression capability 并发送零长度 profile。协商 capability 与 profile 缺失/多余或选择未被 Client 提供的 profile 都是连接级 `ProtocolViolation`。

`ISharpLinkCompressionProvider.WireProfile` 表示完整的 wire profile，不是结构化参数协商。只影响发送端成本/压缩比而不影响解码的 tuning 可以在两端不同；dictionary identity、必须支持的 window/profile、integrity mode 或其他影响解码兼容性的配置必须编码进唯一 profile，并作为不同 Provider 参与现有优先级协商。对同一 profile 配置不兼容解码参数属于 Provider 配置错误。

对端缺少任一 required capability 时，Server 返回 `Unimplemented` 错误并关闭连接。认证载荷不得超过握手/metadata 上限。

## Session binding 与逐调用流准入

`ContractManifest` payload 为 `generation:int64 + count:int32 + serverInstanceId:16B +
registryEpoch:16B`，随后每条为 `contractId:int64 + hashHigh:uint64 + hashLow:uint64`。
GUID 使用 `Guid.TryWriteBytes` 默认布局，必须非空；ID 必须非零、唯一且按有符号 ID 严格递增，
hash 不得为空。Manifest 观察、连接 Ready 和某个契约已绑定是不同边界。

`BindContract` / `BindAck` 均不压缩、无 flags。以下大小均不含 15 B 帧头：

```text
Bind (40 B): ContractId:int64 + hashHigh:uint64 + hashLow:uint64
             + requirement:int64 + receiptPresent:int64
optional receipt (72 B): serverInstanceId:16B + registryEpoch:16B
             + generation:int64 + ContractId:int64 + hashHigh:uint64 + hashLow:uint64
             + registrationId:int64
Ack (40 B): status:int64 + ContractId:int64 + BindingId:uint64
             + registrationId:int64 + publicationGeneration:int64
```

requirement 为 Compatible=0、MinimumVersion=1、ExactRegistration=2；receiptPresent 只能为 0/1，
后两种要求 receipt。status=0 表示成功，token/registration 必须非零；拒绝时二者均为零。
服务端先在注册事务边界验证完整身份/receipt 并安装表项，再回复；Bind 不激活 Service、不执行
业务 Codec、不预留普通调用容量。公开控制面与 receipt 约束见 [调用文档](calls-and-streaming.md)。

BindingId 只对应签发它的物理 Session 和精确 registration，不能跨重连/蓝绿 redirect 搬用。
服务端每个请求仍比较完整 token、取得该 registration 的生命周期保护，并验证方法 facts/shape，
然后才进入资源准入、解压、Codec 和调用 pipeline。未知/退休 token 不执行业务；退休 gate 是最终
准入切点，不依赖失效通知及时到达。槽位复用必须更换完整 incarnation，耗尽不得 wrap 后重用。

当前实现每 Session 最多 64 binding slots、每 binding 最多 256 methods；同一 Client/Server 的
跨 Session 总 binding 配额为 128、进行中 control operations 为 64。Client live sites 和 Prepare
waiters 各最多 64。操作、迟到 Ack/失效状态和等待均有界；Draining 不接受新 Bind，已开始流的
RequestAccepted/取消/终止仍可完成。具体缓存不是可由应用设置的远端身份或授权能力。

两向流在本次调用资源/路由、activation 和现有 auth/interceptor terminal 就绪后，先发送
`RequestAccepted` 再进入生成 Stub。ClientStreaming/Duplex producer 只有在其原 pending generation
仍存活且取得 start claim 后才运行；取消、deadline、Drain 和迟到 Ack 不得复活 producer。
ServerStreaming/Duplex 的最终成功仍由 `StreamComplete` 表示。

OneWay 输入不使用逐调用 Ack。专用帧的 count 和 16 B route 均独立且未压缩；服务端按此 count
安全丢弃被拒调用的输入、归还 credit 并清除 route。与已绑定 facts 冲突、count 非法或无法有界
清理时关闭 Session；合法有界的未知方法可 drain，不需要调用未知 Stub/Codec 推断形态。

## 压缩载荷

压缩只覆盖 Generated Codec 产生的业务 payload，路由、TimeBudget、metadata 和 stream ID 始终保持未压缩，便于在分配前完成路由、资源与长度校验：

```text
Request            = route/TimeBudget/metadata envelope + originalBodyLength:uint32 + compressedBody
OneWayInputRequest = count:uint8 + Request envelope/body（count 也不压缩）
Response   = originalBodyLength:uint32 + compressedBody
StreamData = streamId:uint16 + originalItemLength:uint32 + compressedBody

`compressedBody` may be empty when the negotiated provider profile defines a valid zero-byte representation; Core validates the `originalLength` envelope and delegates representation validity to the provider.
```

`original*Length` 必须非零，并且与未压缩固定前缀相加后不超过协商的 frame 上限；框架在租借有界 owner 之前完成该检查。Provider 成功返回即承诺完整消费输入；Core 直接从其有界 writer 核对实际写入长度与声明的原始长度。Stream flow-control 始终按原始 item 字节计费，防止高压缩比数据绕过接收窗口。

发送端仅在业务 payload 至少 1024 B、至少节省 64 B 且节省比例不低于 5% 时选用候选压缩帧；三个阈值均可配置。候选无收益时立即归还候选 owner，原始 owner 原样交给现有 SendPump。SendPump 不识别压缩，也不会同时持有两个候选。

Core 不内置具体压缩算法，也不为算法增加私有 framing/checksum。`TryCompress` 返回 `true` 表示完整 representation 已写入，返回 `false` 只表示它无法在 Core 给定的 `maxOutputBytes` 内完成，Core 会丢弃候选并发送 raw。`Decompress` 正常返回表示完整消费 `compressedBody` 且没有忽略 trailing bytes；malformed、truncated、profile integrity failure 或超过输出上限必须确定性失败。具体 profile 的 silent-corruption 检测能力由该 profile/provider 自身定义；未来官方 profile 若要求强完整性，应使用该 wire format 自带的完整性机制，而不是把算法补偿逻辑放回 Core。

未协商却设置 `Compressed`、非法固定前缀或原始长度属于连接级 `ProtocolViolation`。已协商载荷的截断、损坏、尾部数据或输出长度不符映射为当前调用/流的 `DataLoss`；自定义 Provider 的未预期异常映射为该调用/流的安全 `Internal`。这两类调用级错误不关闭健康连接。

## 服务端主动接入控制

Admission control 不增加 wire capability，也不改变客户端发送格式。服务端在未创建 Service、Scope、Codec 状态或执行 Interceptor 前，按 `Global → Contract → Method → Partition` 累计取得全部 permit。无等待策略、队列数量已满、队列保留字节已满、速率或并发限制拒绝时，普通调用返回 `ResourceExhausted`；Server Draining 时返回 `Unavailable`。拒绝当前调用不会关闭连接，过载解除后同一健康连接可继续调用。

等待中的 Request 保留有界的原始 head，并保护其精确 registration。两向 ClientStreaming/Duplex
在本次 RequestAccepted 前不启动 producer，不能再把这段等待描述为两向输入预准入 spool。
明确启用排队的 OneWay 输入仍可在准入前生产；按独立 count 预留 stream ID，并按到达顺序有界
保留 StreamData/StreamComplete。压缩 item 在保留前验证 originalItemLength，按 wire bytes 计入
预准入字节预算，取得 permit 后才解压/调用 Codec。溢出、取消、deadline、断连或强制 Drain
终止该调用并释放物理 owner、route 和 credit；已获准的原代调用不得改投 replacement。

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
