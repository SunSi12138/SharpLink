# SharpLink 架构说明

## 分层视图

```text
Application
  -> SharpLink.Client / SharpLink.Server / SharpLink.Hosting
    -> SharpLink.Runtime
      -> SharpLink.Abstractions

SharpLink.Sdk
  -> IService / RpcContract / RpcService / Oneway / Timeout / SharpLinkCallOptions

SharpLink.Generator
  -> 扫描契约与服务，生成 Proxy / Stub / Codec / Assembly Manifest

SharpLink.Serializer.SharpPack
  -> 声明通用 Codec Adapter registration，并为复杂对象图提供 manifest-scoped SharpPack Context
```

## 各模块职责

- `SharpLink.Abstractions`
  - Protocol v2 模型（`ProtocolV2FrameType` / `ProtocolV2FrameFlags` / `ProtocolV2Constants`）
  - 核心抽象（`IRpcChannel`、`IRpcStub`、`IClientTransportFactory`、`IServerTransportListener`、`ITransportConnection`、`IRpcSession`、`IRpcCodec`）
  - 结构化错误模型（`SharpLinkException` / `SharpLinkErrorCode`）
  - Assembly Manifest、弱 Catalog、结构化程序集注册结果与 Client/Server 公共接口

- `SharpLink.Runtime`
  - `RpcSession`、`StreamManager`、`Request/Stream` 调度基础设施
  - Context 所属的 `IRpcCodecProvider` 与内置不可变编解码器
  - 传输实现（Socket、NamedPipe、AnonymousPipe、SharedMemory 的 client factory / server listener / 独立 connection）
  - Protocol v2 帧编解码、发送泵、池化缓冲与并发容器

- `SharpLink.Sdk`
  - 只承载契约层标记和最小公共类型
  - 当前不承载 Builder；Builder 位于 `SharpLink.Client` 和 `SharpLink.Server`

- `SharpLink.Client`
  - `SharpClientBuilder`
  - 连接、心跳、请求跟踪、超时/取消管理
  - 每次连接通过 `ISharpLinkClientAuthenticator` 异步创建认证 payload
  - 原子连接状态机、自动重连、`ConnectAsync / StopAsync` 生命周期
  - 承载生成代理最终调用的 `IRpcChannel` 实现

- `SharpLink.Server`
  - `SharpLinkServerBuilder`
  - 连接接受、会话生命周期、服务分发
  - `ISharpLinkServerAuthenticator` 与显式 `RequireAuthentication()`
  - 将当前 `sessionId + requestId + method descriptor + peer + 认证上下文 + deadline + metadata` 挂入 `SharpLinkCallContext`
  - 通过 `SharpLinkAuthorization` 在服务方法内部执行 `scope / tenant / expiry` 校验
  - 调用 `IRpcStub` 执行真实服务方法

- `SharpLink.Hosting`
  - `AddSharpLinkServer()` / `AddSharpLinkClient()`
  - `HostedService` 托管封装与 `ISharpLinkClientAccessor`

- `SharpLink.Generator`
  - 扫描 `[RpcContract]` 接口与 `[RpcService]` 实现
  - Contract 程序集生成 Descriptor、Proxy、contract-based Stub 与 Codec；Service 程序集生成 Descriptor、Activator、生命周期与依赖
  - 每程序集生成唯一 Manifest、定位特性、Module Initializer 和 SHA-256 wire/schema 指纹
  - 输出编译期诊断（取消令牌、超时、泛型、契约继承、服务声明和静态 Artifact 冲突等）
  - 只读取 `RpcCodecAdapterRegistrationAttribute` 的 Roslyn metadata；不硬编码或加载第三方序列化框架

## Unary 调用链

1. 业务代码通过 `client.Get<TContract>()` 获取生成的 Proxy。
2. Proxy 将参数写入 payload，并调用 `SharpLinkClient` 的 `IRpcChannel` 实现。
3. Client 发送 `Request` 帧（包含 `contractId / methodId / requestId`）。
4. Server 根据 `interfaceHash` 找到 `IRpcStub` 与目标服务实例。
5. Stub 解码参数并调用真实服务方法。
6. 返回值编码为 `Response` 帧。
7. Client `RequestManager` 唤醒对应等待调用。

## 流式链路

- 客户端流：Client 按 `(requestId, streamId)` 发送 `StreamData / StreamComplete`
- 服务端流：Server 使用相同协议回推流元素，Client 侧 `StreamManager` 分发到对应 `Channel`
- 双向流：客户端流上传与服务端流下发同时存在
- 多流参数：同一请求内通过不同 `streamId` 区分

## Stream 字节流控

- Protocol v2 握手协商 `FlowControl` capability 以及 stream/connection receive window。
- 每个 `StreamData` 在进入 SendPump 前同时预留两级字节额度；额度不足的 producer 按 FIFO 异步等待，并受 cancellation、deadline 与 session 终态控制。
- 单个 item 只要未超过协商帧上限，可以在空窗口上临时借用一次，消费后必须完整归还。
- dispatcher 保存 decoded item 对应的 encoded byte count；消费者成功取走或丢弃 item 后累计 credit，达到半窗口时发送 `WindowUpdate`。
- 未知或已取消 stream 的迟到数据不会创建新 dispatcher；窗口溢出、重复 credit 和连续越窗均作为 `ProtocolViolation` 关闭连接。

## 客户端连接池

- 每个 Client endpoint 拥有一个冻结配置的有界池；默认 `MinConnections=1 / MaxConnections=1`。
- 单连接快路径直接返回唯一 session；多连接使用 power-of-two choices，从两个随机候选中选择 active request 较少者。
- 请求 ID 与创建时选中的 session 绑定，Unary、client/server stream 与 duplex 的响应、取消、超时和断连都通过同一绑定释放 active 计数。
- 只有当前候选已有在途请求时才合并触发一个扩容 worker，不能按每次调用创建连接。
- `GoAway` 将单条 session 标为 draining 并立即从选择快照移除；已有请求完成后释放该连接，池在后台恢复最小连接数。
- Client Stop 取消并等待 connect、reconnect、expand、heartbeat 与 read-loop worker，再释放所有 session 和 transport factory。
- Client/Server 收到完整帧时同时维护诊断用 UTC `LastActive` 与内部单调时间戳；heartbeat timeout 只按单调 elapsed time 判定，不受系统墙钟校时或调用方修改诊断属性影响。

## 0.7.x endpoint 拓扑与韧性

- 固定单 endpoint 仍是默认快路径；只有显式 `UseEndpoints` 或 `UseEndpointResolver` 才会创建 endpoint candidate、selector 和后台 topology worker。单个 static endpoint 在 Build 时折叠回固定快路径。
- static 和 dynamic cluster 都以不可变 Ready candidate snapshot 供调用路径读取；端点增减或 Ready 边界变化由单 writer 发布，选择路径不获取 topology writer lock。多 endpoint 默认 P2C，可显式选择 Random、RoundRobin、LeastPending 或同步自定义 selector。
- Resolver snapshot 按版本验证并原子 reconcile：新 ID 创建 generation，Address/Authority 变化替换 generation 并排空旧连接，仅 Attributes 更新保留连接。空 snapshot 合法；resolver 故障或 Watch 结束保留 last-good topology 并退避恢复。
- Retry 默认关闭，只对显式 `[Idempotent]` Unary 生效；拦截器按 logical call 执行一次，每次 attempt 重新选择 endpoint 并共享入口冻结的绝对 deadline。任何 Streaming 或 OneWay 不会被自动重试。
- Endpoint admission 和 Circuit Breaker 只决定是否发起新 attempt，不会修改物理 connection 的 Ready 语义。Breaker 状态按 endpoint generation 隔离，以 monotonic time 惰性推进，HalfOpen 使用原子 probe permit。
- `SharpLinkTelemetry` 无 listener 时不创建 TagList、Activity 或动态字符串。endpoint 路径提供 active/ready/draining endpoint、resolver update/failure、active/retiring connection、attempt、retry、admission rejection、breaker open 的低基数指标；endpoint ID、address 和 authority 只出现在 Activity 或结构化日志中。

## 取消与超时

1. 调用侧 `CancellationToken`、monotonic deadline 或 stream consumer early-break 通过客户端 PendingCall 的单一 CAS 终态仲裁。
2. Client 在协商 protocol minor 2 的 `CancellationReason` capability 后，分别发送 `UserCancellation`、`DeadlineExceeded` 或 `ConsumerAbandoned`；旧对端继续使用空载荷 Cancel。
3. Server 先 CAS 发布稳定终止原因，再取消 invocation CTS，保证业务取消回调看到的原因已经确定。
4. 没有业务 Token 的调用不创建 invocation CTS；客户端仍按 deadline 结束，服务端抑制迟到响应并观察 Task 到真实结束。
5. 每条服务端连接用一个 Timer 扫描有界调用表；所有响应在发送前再次用 monotonic deadline 仲裁。

## 错误传播

- 远端二进制错误码会映射为同码 `SharpLinkException`；错误消息受 64 KiB 上限约束
- 连接关闭、心跳超时、协议异常也会统一映射为带错误码的 `SharpLinkException`
- 用户取消保留 `OperationCanceledException`；deadline 到期映射为 `DeadlineExceeded`
- `StreamManager` 与 pending request 会共享同一错误对象，避免流式路径退化成普通字符串异常
- 握手拒绝现在也支持结构化传播；服务端可通过 `SharpLinkAuthenticationResult` 返回明确错误码与消息
- 服务方法执行期间可以通过 `SharpLinkCallContext.Current` 读取当前认证上下文，而不需要改动生成代理/Stub 签名
- 当前认证上下文已覆盖 `subject / tenantId / scopes / expiresAt / claims`
- 授权 helper 抛出的 `SharpLinkException` 会通过 `Response / StreamComplete(Error)` 保留原始错误码

完整线协议见 [`protocol-v2.md`](protocol-v2.md)。

## 序列化策略

- 默认内置基础类型与 blittable 容器 Codec；RPC 可达的封闭 DTO/集合由 Source Generator 生成字段 ID Codec
- 进程 Catalog 只保存有界、可清理的弱 Manifest 引用，collectible ALC 不会被它强引用
- 每个 Runtime Context 在 Build 时导入已加载 Manifest 快照；Build 后插件通过实例的 `RegisterAssembly` 原子发布新快照
- 普通 DTO 继续优先使用原生 Codec；`[SharpPackable]` 通过扩展包 registration 自动选择 SharpPack Adapter
- 没有 selector Attribute 的类型使用类型级或程序集级 `[RpcCodecAdapter(...)]` 显式绑定；安装 Adapter 包不会自动 fallback 或改变 wire format
- generated factory 直接发出闭合 `IRpcCodecAdapterScope.CreateCodec<T>()`；不使用 `MakeGenericType`、`Activator`、运行时类型扫描或非泛型序列化 API
- Adapter Scope 按 `Runtime Context × Manifest instance × AdapterId` 创建；同组 Codec 共享 Scope，不同 Runtime、Manifest 和插件代际互相隔离
- 显式 `UseCodec` 始终优先于 Manifest Adapter，且 Runtime 不释放调用方 Codec 或自定义 serializer Context
- Codec cache 绑定 Manifest registration identity；replace 发布新代后，旧模块清理不会删除新 Codec
- Codec Provider、Buffer Pool、状态容器配置都冻结在各自的 `SharpLinkRuntimeContext` 中，不允许 Builder 覆盖进程级可变配置

完整 Adapter SPI、事务发布和动态卸载设计见 [`architecture-0.7.11.md`](architecture-0.7.11.md)。

## 平台约束

- Unix/macOS 上 `NamedPipe` 由 .NET 映射到 Unix Domain Socket 路径
- 当前运行时会对过长的 pipe name 做确定性缩短，避免触发路径长度限制
- `AnonymousPipe` 适合本机协同进程，不适合跨主机场景
- `SharedMemory` 只支持同机同用户。命名管道是权限边界和控制通道；数据不经过控制通道。
- 每条共享内存连接拥有一个 4 KiB 版本化小端头部和两个 SPSC 环。读写游标、等待标志与关闭位按 128 字节隔离；文件映射只在双方 nonce、版本、容量和长度全部校验后开放。
- Unix/macOS 在双方确认映射后 unlink 文件；Windows 使用 delete sharing 与 `DeleteOnClose`。新建映射前只清理能够独占打开的遗留 `.shm` 文件，不删除活跃连接资源。
- Writer 优先直接返回映射内存；只有回卷、空间不足或已有待处理数据时使用有界池化 spill。累积 spill 与超环 staging 都使用池化 sequence segments，避免扩容时重复复制已积累字节。Reader 直接返回映射上的 `ReadOnlySequence<byte>`，只有跨环且协议尚未消费的半帧进入 staging。
- 通知后端当前统一为 `named-pipe-control`。共享等待标志使用“设置后重新检查”：只有对端实际登记等待时才发控制信号，登记前发生的游标变化由重新检查观察，因此不依赖过期通知。data/space 可在一次 bitmask 写中合并，进程内 waiter 使用可复用的单消费者 ValueTask source。

## 客户端 Unary 热路径

- 静态、无遥测、无 interceptor、非 `WaitForReady` 的 Unary 调用直接把池化 `RpcRequestOperation<T>` 暴露为 `ValueTask<T>`；响应、错误、取消、deadline 和断连仍由 PendingRequestTable 的单一完成仲裁负责。
- `WaitForReady` 保持独立异步慢路径，连接尚未就绪时仍按 deadline 与取消等待；默认调用不会为这个未启用能力创建包装状态机。
- `RpcRequestOperation<T>` 与 PendingCall 使用有界、可清理的并发队列复用。队列槽位复用不改变请求 ID、资源上限或回收时清除 continuation 的要求。
- Server `SharpLinkCallContext`、认证上下文和 `AsyncLocal` 流动没有被性能快路径绕过；它们是当前剩余稳态分配的主要来源。

## TCP TLS

- `SocketClientTransportFactory` 在 TCP connect 后执行 `SslStream.AuthenticateAsClientAsync`，成功后才进入 RPC handshake。
- Server accept loop 只负责快速接收 socket；每条 accepted connection 在独立、被追踪的生命周期任务中执行 TLS，慢客户端不会串行阻塞后续 accept。
- TLS handshake 默认 10 秒并可独立配置；timeout、server stop 与 caller cancellation 都会释放 socket、SslStream 和 Pipe。
- mTLS 直接使用 `SslServerAuthenticationOptions.ClientCertificateRequired` 与客户端证书集合；默认服务器证书验证不被框架放宽。
- 非 TCP transport 不创建 `SslStream`。协商后的 TLS protocol/cipher 可用于日志和后续 telemetry，但认证 payload、token 和证书敏感数据不写日志。

## 连接认证

- 默认无 provider 时为 Anonymous；要求身份的 Server 必须显式调用 `RequireAuthentication()`，否则 Build 不允许遗漏 provider。
- Client provider 每次连接尝试都会在 RPC handshake timeout 内重新执行，断线重连不会复用已过期 payload。
- Server provider 接收复制后的有界二进制 payload、connection ID 与 peer endpoint；异步执行期间不会持有 Pipe buffer。
- provider 返回的 `SharpLinkAuthenticationContext` 归属 session，并在每次调用创建 `SharpLinkCallContextSnapshot` 时传递。
- handshake 自动拒绝已过期 context；每次业务调用会再次拒绝已过期身份，Server Interceptor 和 `SharpLinkAuthorization` 可执行更细粒度的 scope/tenant 策略。
- provider 未映射异常记录无 payload 的结构化日志，并向客户端返回不含内部细节的 `AuthenticationRejected`。

## 调用拦截与异常边界

- Client/Server interceptor 按 Builder 注册顺序在 Build 时冻结；空 pipeline 直接进入生成 invoker/stub，不创建 delegate 链。
- Client context 可替换 `SharpLinkCallOptions` 以增加 metadata，也可返回 `SharpLinkClientInvocationResult` 短路调用。
- Server context 包含 method descriptor、request ID、deadline、metadata、peer、auth、status 与 elapsed，适合授权、限流与审计。
- `IRpcExceptionMapper` 属于 Server 实例。默认 mapper 保留显式 `SharpLinkException`，其余业务异常统一为不含内部消息的 `Internal`；Unary 与 stream 共用该边界。
- `[Idempotent]` 只写入生成 descriptor，核心不会自动重试；新版 0.7 Resilience 扩展只会把该标记作为 Unary 重试资格。

## 遥测

- 公共 `SharpLinkTelemetry` 暴露 `SharpLink.Client`、`SharpLink.Server` 两个 `ActivitySource` 和名为 `SharpLink` 的 `Meter`。
- Activity 只在 source 有 listener 时创建，并携带 contract ID、method ID、method kind、server request ID 与结构化状态；不写入 payload、token、证书或业务异常消息。
- Meter 覆盖 active connections、reconnect、calls started/completed/failed/active/abandoned/duration、sent/received bytes、send queue bytes、pending requests、active streams、late responses、protocol/auth/resource-exhausted failures，以及共享内存协商容量、spill bytes、waits 和 notifications。可选详细诊断还区分 direct/spill 原因、staging、复制、通知请求/合并和游标刷新；这些高频计数不用于正式计时。
- abandoned call 带低基数 termination reason；迟到响应逐次计数，但每连接 Warning 使用五秒限频窗口并报告被抑制数量。
- Counter/Histogram/Activity 均先检查 listener/instrument；无 listener 时不创建 TagList、Activity、Stopwatch 对象或 observer state machine。
- 日志全部使用 `LoggerMessage` source-generated 方法；普通日志不包含 payload、token 或证书内容。

## 自动注册、服务生命周期与排空

- Server Build 合并弱 Catalog 快照、Builder 筛选和 `ReplaceService`，完成全量验证后一次发布实例 Registry；一个 Contract 只能有一个 Owner。
- 默认 `Singleton` 延迟且线程安全地创建一次，不建立调用 Scope。`Connection` 按物理连接和 registration 独立惰性创建 Scope，断连后等待相关调用结束再释放。`Call` 每次调用创建一个实例和 Scope，Streaming 保持到整条流真正终止。
- Generator Activator 直接调用选定构造函数并从当前 Scope Provider 解析普通依赖；Microsoft DI 继续管理依赖，根 RPC 服务不再使用 `ServiceLifetime` 表示公共生命周期。
- Generator 以稳定顺序输出 JSON 契约 Manifest；可选 `SharpLinkContractBaseline` 只在编译期执行一次完整差异分析，运行时替换仅验证生成 Manifest、route identity 与 registration ownership，不复制源码级兼容规则。
- `ReplaceService` 实例始终由调用方持有且是 Singleton；factory 产物由 SharpLink 释放。激活失败也会释放已经创建的 Scope。
- Protocol minor 1 引入 health-check capability，minor 2 引入带原因 Cancel，minor 3 在握手中协商唯一压缩 Provider；`HealthCheck/HealthResponse` 使用非零 correlation ID 和固定一字节状态，不进入业务 stub、interceptor 或服务并发额度。
- 压缩在 Generated Codec 序列化之后、SendPump 之前运行；候选无收益即归还。接收端先验证未压缩 envelope 和原始长度，再租借精确有界 owner，调用/stream dispatch 完成后归还。未启用时 Session 热路径只增加一个可预测的空引用分支，SendPump、静态路由和 Codec 热路径不增加锁。
- 主动 admission 默认关闭，并在 Service/Scope/Codec/interceptor 之前累计取得 Global、Contract、Method 与可选 Partition permit；同步 AttemptAcquire 是启用态快路径。异步等待同时受总 call/byte 预算、deadline、取消、断连和 Draining 约束；客户端流以生成的 `ClientStreamCount` 预留 stream ID，压缩 frame 按 wire bytes spool，permit 到达后才解压和 dispatch。
- 分区池只在 miss/release 时机会式回收，无清理线程；持有 permit、waiter 或 stream spool 的 entry 不可回收。所有 lease 都挂在既有 ServerCallCancellationState 上，沿 Unary、OneWay 和完整 Streaming 生命周期一次释放；未启用时只读取空 controller 引用，不创建 Task、状态机、TagList 或每调用对象。
- Server 状态映射为 Starting/Stopped/Faulted=`Unhealthy`、Running=`Ready`、Draining=`Draining`。Hosted readiness 直接读取 Server 原子状态，Client accessor 只在至少一条连接 Ready 后发布。
- Stop 先进入 Draining，再停止 accept 并发送强制 flush 的 GoAway；grace 内等待 active calls，超时后取消 session 调用，最后等待后台任务并释放 service/provider。

## 动态程序集 Registry

- Client/Server 各自持有带 generation 的原子不可变快照。注册在 RPC 路径外构造候选，只在短 writer gate 内重检 generation 和生命周期，然后用一次原子写发布；读路径不获取注册锁。
- 原子替换在 writer gate 内从当前快照同时移除旧 registration route 并加入新 route，再用一次写发布；旧模块随后进入既有 Draining 状态机，已取得的调用和流租约不迁移。
- Assembly 使用对象引用身份；同一对象重复注册失败，不同 ALC 的同名程序集可进入验证，但 Contract/Method 路由、Codec 和 Service 冲突仍按 ID、名称、schema 与完整指纹拒绝，且不部分提交。
- 动态模块状态为 `Running -> Draining -> Released/DrainTimedOut`。动态调用持有固定 stripe 的缓存行隔离租约；静态项不计数，也不进入动态锁。
- 普通注销的 Draining 期间模块继续占有路由；原子替换则在进入 Draining 前先发布新 route。排空超时只取消该模块调用和流；不合作业务保留其已取得的资源，直到后台观察到计数归零后再释放框架持有的 Manifest、Proxy、Stub、Codec、Service、Scope 与 Timer 引用。
- 依赖模块必须先注册且后注销；Stop/Dispose 与显式 Unregister 共享同一个幂等排空操作。NativeAOT 通过 feature switch 移除动态定位/计数路径，不提供反射 fallback。
