# 更新日志

本文档记录项目的重要变更，格式参考 Keep a Changelog，并遵循语义化版本。

## [Unreleased]

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
