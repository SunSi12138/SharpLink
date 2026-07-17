# 更新日志

本文档记录项目的重要变更，格式参考 Keep a Changelog，并遵循语义化版本。

## [Unreleased]

## [0.6.7] - 2026-07-17

### 新增

- 每条物理连接独立拥有 `ClientConnection`、pending table、request ID、monotonic deadline timer、取消源、active count 与 Ready/Draining/Closed 状态。
- `[NonCancellable]` 显式声明服务方法不支持协作取消；Source Generator 通过 `SHARPLINK004` 警告未声明 `CancellationToken` 的 RPC 方法。
- `sharplink.calls.abandoned` 指标与限频结构化日志，记录客户端已放弃但服务业务仍在执行的调用。

### 变更

- Response、error、用户取消、deadline、断连、GoAway、send failure、stream complete 与提前停止消费统一经过每连接 `PendingCallTable.TryComplete` 仲裁。
- 每连接只使用一个 monotonic timer 扫描有界 pending table；正常完成不再进入旧 timeout scheduler 的 Schedule/Cancel 锁。
- 默认 Unary timeout 仍为 30 秒并固定映射为 `SharpLinkException(DeadlineExceeded)`；`DisableRequestTimeout()` 提供真正无客户端默认 timeout 的显式入口。
- 客户端 deadline 与服务执行取消解耦：没有 `CancellationToken` 的服务调用会在客户端超时后标记 Abandoned、抑制迟到响应，并在业务任务真实结束后释放 admission 与 DI scope。
- Server/duplex stream 提前 Dispose 会发送 Cancel，并一次性释放 pending slot、dispatcher、producer、credit waiter 与 active count。

### 性能

- 同步且 `[NonCancellable]` 的服务调用不租用 cancellation state、不进入服务端 cancellation map；只有支持协作取消或真正异步未完成的调用才注册状态。
- 同机五轮 A/B 中位数相对 `v0.6.6`：c1 QPS +0.59%，c128 QPS +0.33%，c128 P99 -16.82%，`Rpc_Add` allocation 保持 672 B/op。
- 已撤销“所有服务调用无条件注册 cancellation state”的实验；其拆分测量导致 c128 相对客户端候选回退约 10%。

### 修复

- 迟到 response 不再需要永久 tombstone，也不会完成已复用的 request ID 或关闭健康连接。
- 修复提前退出 server/duplex stream 后客户端 pending、dispatcher 与 active count 泄漏。
- 修复只因请求携带 deadline 就把任意 `OperationCanceledException` 误报为 `DeadlineExceeded` 的错误分类。
- Cancel、deadline、response 与 disconnect 竞态只允许一个终态，operation 仍在调用方 `GetResult` 后才回池。

完整 A/B 环境、失败实验和最终结论见 `doc/performance-0.6.7.md`。

## [0.6.6] - 2026-07-17

### 新增

- LoadTest、StreamLoadTest 与热路径 Micro Benchmark 增加可重复 JSON 证据，记录 commit、机器、Runtime、GC、Transport、Profile、连接池、payload 与并发度。
- `IRpcBufferWriterPool.Rent(int maxWrittenBytes)` 与 session 协商后的出站帧硬上限。
- Server Interceptor 异步等待期间使用有界 ArrayPool owner 持有业务 arguments，避免越过 `PipeReader.AdvanceTo` 后读取复用内存。

### 性能

- SendPump 没有 admission waiter 时跳过容量通知锁；默认单连接池达到上限时跳过扩容状态锁。
- Generated Stub 的多段固定宽度参数改用有界 stack scratch，不再为每个参数分配临时数组。
- `SharpLinkCallContext` scope 改为值类型；基准从约 104 B/op 降至 72 B/op。
- Parser 只做帧结构验证，Metadata/Error/Handshake 等语义 payload 在消费位置解析一次；Metadata parser 基准降为 0 B/op。

### 变更

- 删除进程级 `BufferWriterPool`、`RuntimeConcurrency`、`RpcCodecRegistry`、`RpcCodec` 兼容入口；Codec、Pool 与状态容器配置只属于构建它们的 Client/Server Context。
- 删除旧 Client 调用排列组合和 CallOptions wrapper；生成代理只使用 Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类 `IRpcChannel` invoker。
- 测试与示例迁移到实例级 Runtime Context、`SharpLinkBufferWriterPool` 和生成调用链。

### 修复

- Client/Server 所有业务帧、流帧与错误帧统一执行双方握手协商后的较小 frame limit；本地超限返回 `ResourceExhausted` 且不关闭健康连接。
- 修复启用异步 Server Interceptor 后请求参数可能引用已归还 Pipe buffer 的生命周期问题。

完整 A/B 环境、数据和结论见 `doc/performance-0.6.6.md`。

## [0.6.0] - 2026-07-17

### 新增

- TCP TLS 与双向证书认证，TLS 在 Protocol v2 handshake 前完成并使用独立超时。
- TLS 协议/cipher 结构化日志；默认保留平台证书链和 hostname 校验。
- `ISharpLinkClientAuthenticator`、`ISharpLinkServerAuthenticator`、二进制认证请求与 delegate adapter。
- 显式 `RequireAuthentication()` Build 校验；默认模式保持 Anonymous。
- Client/Server interceptor pipeline、实例级 `IRpcExceptionMapper` 与显式 `[Idempotent]` 方法元数据。
- `SharpLink.Client` / `SharpLink.Server` ActivitySource 与 `SharpLink` Meter；覆盖连接、调用、字节、队列、pending、stream 和失败指标。
- instance/type/factory 三类服务注册、Singleton/Scoped/Transient 生命周期与宿主 DI scope。
- Protocol v2 health-check capability、`CheckHealthAsync`、本地 readiness 和 Microsoft health checks。

### 变更

- 删除 string/bool authenticator Builder API；认证 payload 在每次重连时异步创建并受 handshake 上限约束。
- 认证上下文挂入每次服务调用；handshake 自动拒绝已过期 context，授权 helper 可在调用前再次校验 expiry/scope/tenant。
- 未注册 interceptor 时生成调用继续直达泛型 invoker；注册后客户端可修改调用选项或短路，服务端可鉴权、限流与审计。
- 未映射业务异常默认只公开 `Internal` 和通用消息；详细错误需要显式启用，stream 错误使用同一 mapper。
- Activity/Meter 无 listener 时不构建 tag collection、Activity 或调用 observer；结构化日志继续使用 `LoggerMessage` 预编译路径。
- 默认服务生命周期保持 Singleton 热路径；Scoped/Transient scope 覆盖完整调用或 stream，并在异常、取消、断线和停机时释放。
- Server 停机顺序固定为 readiness=false、停止 accept、GoAway、等待在途调用、超时取消、flush 与资源释放。

### 修复

- Source Generator 辅助 request/stream 类型加入契约名前缀，多个接口出现相同 method hash 时不再发生编译期类型冲突。
- NativeAOT 服务注册显式保留 DI 所需 public constructor，避免服务构造器被 trimming 后在首个调用失败。

## [0.5.0] - 2026-07-17

### 新增

- 可异步 admission 的 `PendingRequestTable`，完整 64 位 request ID 匹配与统一完成仲裁。
- `PooledByteBufferWriter`、明确的 frame owner 生命周期与 Context 有界 writer pool。
- Source Generator 原生 DTO/闭合集合 Codec、稳定字段 ID、未知字段跳过、required 校验与 64 层类型图边界。
- `[RpcSerializable]`、`[RpcMember]`、`[RpcIgnore]`、`[RpcRequired]`、`[RpcExternalCodec]`。
- append-only generated Codec manifest；Runtime Context 在 Build 时冻结 manifest 快照。
- Protocol v2 stream/connection 双层字节窗口、`WindowUpdate` 与单个大帧临时借用。
- 每 Endpoint 有界客户端连接池、压力扩容与 power-of-two choices 连接选择。

### 变更

- 生成代理收敛到 Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类内部 Invoker；静态 descriptor/Codec 避免捕获 delegate。
- `IRpcCodec<T>.Serialize` 统一写入 `IBufferWriter<byte>`；协议回填使用 `IRpcByteBufferWriter`。
- AOT Smoke 不再依赖 MemoryPack，覆盖 class、record、struct、嵌套数组和生成 manifest。
- client/server stream sender 在额度不足时异步等待；消费、取消、超时和断连统一释放额度等待者。
- stream dispatcher 按已编码字节记账，迟到的已取消 stream data 只丢弃并计数，不重建 dispatcher。
- stream 调用固定绑定创建时的连接；`GoAway` 连接停止接收新请求并在变为空闲后退出，其他健康连接继续服务。
- LoadTest 与 StreamLoadTest 支持 `--min-connections` / `--max-connections`。

### 修复

- 长请求跨越 pending table ID 周期、乱序响应、cancel/timeout/disconnect 竞态不会误命中新请求或重复归还 operation。
- client/server stream 的额度等待、取消、迟到数据与 terminal ACK 统一收敛，慢消费者不再驱动无界缓冲。
- 多连接下单条 session 断开只失败绑定到该连接的请求，不影响其他 ready session。

## [0.4.0] - 2026-07-17

本版本合并原实施路线图的 0.4 安全基线与 0.5 Runtime/Protocol v2 重构。

### 新增

- 实例级 `SharpLinkRuntimeContext`、性能预设与不可变配置快照。
- TCP/UDS、NamedPipe、AnonymousPipe 的 Client Factory、Server Listener 和独立 Connection 模型。
- 15 字节固定头的 Protocol v2、能力协商、Ping/Pong、Cancel、GoAway、deadline 与 metadata。
- `SharpLinkCallOptions`、`SharpLinkMetadata`、扩展错误码及服务端调用上下文。
- 字节有界单写者 SendPump、强制 flush marker 与资源耗尽保护。
- Client/Server 原子生命周期、自动重连、断连单次收敛和优雅排空。
- NuGet-only PackageSmoke；`SharpLink.Sdk` 自动携带 Source Generator。

### 变更

- `ConnectAsync` 成功返回、失败抛结构化异常，不再返回 `bool`。
- Client/Server 主生命周期统一为 `StopAsync` / `DisposeAsync`。
- 默认 Unary timeout 为 30 秒；stream 默认不设置 timeout。
- 自定义 Codec、Buffer Pool 与并发配置归属实例 Context，不再互相覆盖全局状态。
- 默认认证明确为 Anonymous；删除默认 Password 和旧字符串错误 wire 格式。
- 所有正式包目标框架更新为 .NET 10。

### 修复

- Parser/Codec 在切片、分配前验证所有网络长度，非法数据稳定映射为协议/数据错误。
- pending request、stream、server concurrency、anonymous pipe offer 与 writer retention 增加硬边界。
- 修复 timeout 调度器取消墓碑导致的吞吐退化，取消节点现在立即从有界堆移除。
- 修复 send/dispose、heartbeat/stop、read/write fault 和 GoAway/new request 等生命周期竞态。
- CI 与 PackageSmoke 使用隔离 NuGet 缓存，避免同版本旧包污染验证。

### 兼容性说明

- 本版本包含公共 API 与线协议破坏性升级，不提供 Protocol v1 兼容层。
- c32 Unary QPS 为前一基线的 96.75%，保留原基线并在 0.5.1 优先处理。

## [0.2.0] - 2026-02-18

### 新增

- 引入 `RpcCodecRegistry`，支持统一注册与复用类型 Codec。
- 引入可选 MemoryPack 回退序列化器。
- 增强 blittable struct 容器编解码。

### 变更与修复

- 区分正常关闭与异常断连日志。
- 修复 Hosting 正常停机噪音与 pending request 失败传播问题。
- 本版本包含破坏性变更。

## [0.1.0] - 2026-02-12

### 新增

- 初始化 Abstractions、Runtime、Client、Server、SDK、Hosting 与 Generator。
- 支持 Unary、OneWay、客户端流、服务端流和双向流。
- 增加示例、Unit/Integration/AOT/Load/Benchmark 测试和 CI 工作流。

### 变更与修复

- 优化流式调用固定分配。
- 修复初始生命周期、取消和断连边界问题。
