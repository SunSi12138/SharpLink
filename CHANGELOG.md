# 更新日志

本文档记录项目的重要变更。

格式参考 Keep a Changelog，并遵循语义化版本（SemVer）。

## [Unreleased]

### 变更
- `SharpClientBuilder` / `SharpLinkServerBuilder` 现在会在常规 `Build()` 路径中应用 `UseSerializer(...)` 配置，不再依赖 `UseRpcSessionFlush(...)` 分支。
- `NamedPipeTransport` 在 Unix/macOS 上会对超长 pipe name 做确定性缩短，以适配底层 Unix Domain Socket 路径长度限制。
- 单向调用（`one-way`）+ 客户端流现在会等待客户端流发送完成后再返回，避免调用方与服务端处理结果之间出现竞态。
- Client/Server Builder 新增 `UseAuthenticator(...)`，支持自定义握手消息与服务端握手校验。
- `ISharpLinkClient` 现在可以通过 `ConnectOrThrowAsync()` 直接抛出握手拒绝原因；`ISharpLinkClientDiagnostics` 可读取最近一次连接失败异常。
- Runtime/Client 侧引入 `SharpLinkException` / `SharpLinkErrorCode`，用于统一描述远端错误、连接关闭、心跳超时、协议异常等运行时失败。
- 服务端 `UseAuthenticator(...)` 现在支持返回 `SharpLinkAuthenticationResult`，可显式携带握手拒绝错误码与消息；原有 `Func<string, bool>` 形式保持兼容。
- 服务端调用期间现在可通过 `SharpLinkCallContext.Current` 读取当前 `sessionId` 与认证上下文（`subject/claims`）。
- `SharpLinkAuthenticationContext` 现已补齐 `tenantId / scopes / expiresAt`，服务方法内可直接读取更正式的身份上下文。
- 新增 `SharpLinkAuthorization`，服务方法可以直接执行 `RequireScope / RequireTenant / RequireActiveToken`，并将结构化授权错误码回传给客户端。
- `SocketTransport` 现在暴露 `ILocalEndPointTransport.LocalEndPoint`，支持 `UseTcp(0, ...)` 后读取实际监听端口，减少测试与冒烟程序中的端口竞态。

### 修复
- 修复 `test/SharpLink.AotSmoke` 使用陈旧 Release 版生成器 DLL 作为 Analyzer，导致生成代码与当前源码脱节的问题。
- 修复 `AotSmoke` 复杂类型编解码未显式注册，导致示例运行失败的问题。
- 修复客户端握手失败后未及时释放底层 session 的资源泄漏问题。
- 修复 `AnonymousPipeTransport` 在本机匿名连接场景下握手成功后过早释放 client handle 本地副本，导致后续 RPC 出现 `Operation canceled/Broken pipe` 的问题。
- 修复 `SharpLink.LoadTestBase` 匿名管道本机模式错误复用了不同传输实例，导致服务端永远收不到已分配会话的问题。
- 修复 `RpcSession.SendPump` 在断管/对象释放时抛出未观察后台异常，导致匿名断连测试无法稳定收敛的问题。
- 修复握手响应消费路径遗漏 `AdvanceTo(...)`，导致同一个握手包被后续读循环误判为“意外包”的问题。
- 修复流与 pending request 在断连/远端错误时退化为普通 `Exception`，现在会保留结构化错误码并稳定 fail-fast。
- 修复服务端握手拒绝包在 session 立即销毁时可能尚未真正刷出，导致客户端误收到“握手阶段连接关闭”而非认证拒绝的问题。
- 修正 `Oneway` 示例在 solution 与文件系统之间的大小写不一致，避免大小写敏感环境下构建失败。
- 收敛 `one-way + client-stream` 集成测试的完成时序断言，避免把 one-way 调用误判为同步服务端确认。

## [0.1.0] - 2026-02-12

### 新增
- 初始化 SharpLink 核心项目结构（`Abstractions/Runtime/Client/Server/Sdk/Hosting/Generator`）。
- 基于 Source Generator 的 RPC Proxy/Stub 生成链路。
- 支持 Unary、Oneway、客户端流、服务端流、双向流调用模型。
- 协议级取消能力与流分发能力。
- 基于 `ILogger` 的日志配置能力。
- `BufferWriterPool` 与可配置池化参数。
- 示例项目：`HelloWorld`、`Streaming`、`HostApplication`、`Cancel`、`Oneway`、`Log`。
- 测试项目：集成测试、AOT Smoke、LoadTest、Benchmarks、UnitTests（TUnit）。
- CI 工作流：`pr-quick`、`nightly`、`release-gate`。

### 变更
- 优化流式调用路径，减少固定额外分配。
- 将协议测试迁移到 `SharpLink.UnitTests`（TUnit），并更新 CI 对应步骤。
- 增强 LoadTest 指标：新增实时滚动 QPS/延迟指标，便于 Grafana 观察波动。

### 修复
- 修复多处生命周期与释放路径上的取消/断连边界问题。
- 修复若干 Builder、日志配置、Demo 编译与运行问题。


## [0.2.0] - 2026-02-18

### 新增
- 引入 `RpcCodecRegistry` 体系，支持统一注册与复用类型编解码器。
- 对于引用类型，可接入 MemoryPack 作为回退序列化器：`RpcCodecRegistry.Initialize(MemoryPackCodec.Resolver)`。
- 增强 `StructCodec`：增加 `blittable` 类型容器的高性能序列化/反序列化支持。 

### 变更
- 编解码流程支持回退到配置的序列化器（`MemoryPackCodec.Resolver`），
  提升兼容性。
- 连接断开处理增加“正常关闭”路径，区分 graceful shutdown 与异常断连。
- 客户端断连日志分流：新增 `LogClientDisconnectedWithError`，用于异常场景单
  独记录错误级日志。

### 修复
- 修复 Host 场景中正常停机被当作异常断连输出的问题
  （`ObjectDisposedException` 日志噪音）。
- 优化断连时 pending request 的失败传播路径，避免关闭流程语义混淆。

### 兼容性说明
- 本版本为破坏性变更。
