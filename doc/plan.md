# SharpLink 版本路线图

本文档是版本号与范围的唯一事实来源。实施顺序见 `todo.md`，协议定义见 `protocol-v2.md`，性能数据见 `performance.md`。

## 总体目标

- `.NET 10 LTS`、Windows/Linux/macOS 与 NativeAOT 一等支持。
- TCP/UDS、NamedPipe、AnonymousPipe 使用统一会话、错误与生命周期语义。
- 默认单连接复用，可配置连接池；默认 `Balanced`，提供 `LowLatency` 与 `Throughput`。
- SDK 内置无反射 DTO Source Generator Codec；MemoryPack 保持可选插件。
- 核心提供 TLS、认证、Interceptor、deadline、背压、健康检查、遥测与优雅停机。
- Discovery、Load Balancing、Retry 与 Circuit Breaker 位于官方扩展包。

## 0.4.0：Runtime 与 Protocol v2 基线（已发布）

- Parser/Codec 安全边界、资源上限、生命周期收敛与 PackageSmoke。
- 实例级 Runtime Context、Transport Factory/Listener/Connection 拆分。
- Protocol v2、字节有界 SendPump、CallOptions、deadline、metadata、GoAway 与自动重连。

## 0.5.0：原生 Codec 与热路径优化（已完成）

1. `0.5.1`（已完成）：PendingRequestTable、异步 admission 与统一完成仲裁。
2. `0.5.2`（已完成）：Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类内部 Invoker。
3. `0.5.3`（已完成）：`PooledByteBufferWriter`、`ArrayPool<byte>` 与明确的 payload ownership。
4. `0.5.4`（已完成）：受限、无反射、AOT 友好的 DTO Codec Generator；MemoryPack 承接复杂对象图和显式自定义类型。
5. `0.5.5`（已完成）：stream/connection 按字节窗口、`WindowUpdate` 与慢消费者背压。
6. `0.5.6`（已完成）：每 Endpoint 连接池、power-of-two choices 与性能门禁工具入口。

## 0.6.0：企业核心能力

1. `0.6.1`（已完成）：TCP TLS、双向证书与独立握手超时。
2. `0.6.2`（已完成）：Anonymous/自定义认证、Authentication Context 与 token expiry。
3. `0.6.3`：Client/Server Interceptor、`IRpcExceptionMapper` 与 `[Idempotent]`。
4. `0.6.4`：OpenTelemetry Activity、Meter 与预编译结构化日志。
5. `0.6.5`：DI 服务生命周期、健康检查、readiness 与优雅排空。

## 0.7.0：官方企业扩展包

1. `0.7.1`：Discovery 与不可变 Endpoint snapshot/watch。
2. `0.7.2`：RoundRobin、LeastPending 与默认 PowerOfTwoChoices。
3. `0.7.3`：只针对 `[Idempotent]` Unary 的 Retry 与 Circuit Breaker。

## 0.8.0-rc：发布门禁

- Unit、Generator、Integration、AOT、Package、Load、StreamLoad、Benchmark 与 Chaos 分层测试。
- Windows/Linux/macOS Transport matrix，Linux/Windows 72 小时长稳与固定 runner 性能基线。
- SourceLink、符号包、确定性构建、SBOM、公共 API diff、协议文档与迁移指南。
- RC 期间不再增加功能，只修复正确性、稳定性、性能回退和文档问题。

## 1.0.0：稳定版

- 无 crash、deadlock、未观察后台异常或非注入调用失败。
- 故障收敛后 pending request、stream 与后台任务在 timeout 内归零。
- NativeAOT 无 runtime reflection fallback；SDK 单包引用即可生成契约与 DTO Codec。
- 固化公共 API 与 Protocol v2；不提供 Protocol v1 长期兼容层。

## 不进入 1.0 的范围

- 非 .NET 客户端与跨语言 IDL。
- HTTP/REST/WebSocket/浏览器网关。
- 分布式事务与服务网格控制面。
- 任意运行时反射序列化。
- Streaming/OneWay 自动重试。
- 内置具体注册中心客户端。
